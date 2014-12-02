using System;
using System.Threading;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using Ixxat.Vci3;
using Ixxat.Vci3.Bal;
using Ixxat.Vci3.Bal.Can;

namespace AE.CANOPEN
{
    public delegate void CANMsgReceivedEventHandler(AECanMessageDisplay msg);
    public delegate void SDOMsgReceivedEventHandler(AESDOMessage msg);
    public delegate void PDOMsgReceivedEventHandler(CanMessage msg,int nodeId,int pdoNumber);

    public static class AESXCANObjects
    {
        public const UInt16 EnableESS = 0x6700;
        public const UInt16 KWCommand = 0x6701;
        public const UInt16 KWRampRate = 0x6702;
        public const UInt16 VarCommand = 0x6703;
        public const UInt16 VarRampRate = 0x6704;
    }
    
    public class AECanMessageDisplay
    {
        public String Timestamp { get; set; }
        public String Identifier { get; set; }
        public String Data { get; set; }

        public AECanMessageDisplay() { }

        public AECanMessageDisplay(CanMessage msg)
        {
            DateTime timestamp = new DateTime(msg.TimeStamp);
   
            Timestamp = String.Format("{0}", msg.TimeStamp); //String.Format("{0}:{1}:{2}", timestamp.Hour.ToString(), timestamp.Minute.ToString(), timestamp.Millisecond.ToString());
            Identifier = String.Format("{0,3:X}", msg.Identifier);

            for (int i = 0; i < msg.DataLength; i++)
            {
                Data += String.Format("{0,2:X} ", msg[i]);
            }
        }
    }

    public class AESDOMessage
    {
        public AESDOMessage() { }

        public uint NodeId { get; set; }
        public uint MajorIdentifier { get; set; }
        public uint SubIndex { get; set; }
        public UInt16 Data { get; set; }

        public AESDOMessage(CanMessage msg)
        {
            NodeId = msg.Identifier - 0x580;
            MajorIdentifier = (uint)(msg[2] << 8) + msg[1];
            SubIndex = msg[3];
            Data = (UInt16)((msg[5] << 8) + msg[4]);
        }
    }

    class IxxatCANOpenUSB
    {
        #region Member variables

        /// <summary>
        ///   Reference to the used VCI device.
        /// </summary>
        static IVciDevice mDevice;

        /// <summary>
        ///   Reference to the CAN controller.
        /// </summary>
        static ICanControl mCanCtl;

        /// <summary>
        ///   Reference to the CAN message communication channel.
        /// </summary>
        static ICanChannel mCanChn;

        /// <summary>
        ///   Reference to the message writer of the CAN message channel.
        /// </summary>
        static ICanMessageWriter mWriter;

        /// <summary>
        ///   Reference to the message reader of the CAN message channel.
        /// </summary>
        static ICanMessageReader mReader;

        /// <summary>
        ///   Thread that handles the message reception.
        /// </summary>
        static Thread rxThread;

        /// <summary>
        ///   Quit flag for the receive thread.
        /// </summary>
        static long mMustQuit = 0;

        /// <summary>
        ///   Event that's set if at least one message was received.
        /// </summary>
        static AutoResetEvent mRxEvent;

        const uint SDOTransmitIdentifier = 0x580;
        const uint SDOReceiveIdentifier = 0x600;
        public const uint TPDO1Identifier = 0x180;
        public const uint TPDO2Identifier = 0x280;
        public const uint TPDO3Identifier = 0x380;
        public const uint TPDO4Identifier = 0x480;
        public const uint MaxCANNodes = 127;

        public event CANMsgReceivedEventHandler CANMsgReceived;
        public event SDOMsgReceivedEventHandler SDOMsgReceived;
        public event PDOMsgReceivedEventHandler PDOMsgReceived;

        public bool InitCANAdapter()
        {
            IVciDeviceManager deviceManager = null;
            IVciDeviceList deviceList = null;
            IEnumerator deviceEnum = null;
            IBalObject bal = null;
            bool success = false;

            try
            {
                deviceManager = VciServer.GetDeviceManager();

                deviceList = deviceManager.GetDeviceList();
                deviceEnum = deviceList.GetEnumerator();
                deviceEnum.MoveNext();

                mDevice = deviceEnum.Current as IVciDevice;

                bal = mDevice.OpenBusAccessLayer();

                mCanChn = bal.OpenSocket(0, typeof(ICanChannel)) as ICanChannel;
                mCanChn.Initialize(1024, 128, false);

                mReader = mCanChn.GetMessageReader();
                mReader.Threshold = 1;

                mRxEvent = new AutoResetEvent(false);
                mReader.AssignEvent(mRxEvent);

                mWriter = mCanChn.GetMessageWriter();
                mWriter.Threshold = 1;

                mCanChn.Activate();

                mCanCtl = bal.OpenSocket(0, typeof(ICanControl)) as ICanControl;

                mCanCtl.InitLine(CanOperatingModes.Standard | CanOperatingModes.ErrFrame, CanBitrate.Cia250KBit);
                mCanCtl.SetAccFilter(CanFilter.Std, (uint)CanAccCode.All, (uint)CanAccMask.All);

                mCanCtl.StartLine();
                success = true;
            }
            catch (Exception)
            {
                Console.WriteLine("Unable to connect to CAN adapter");
            }
            finally
            {
                DisposeObject(deviceManager);
                DisposeObject(deviceList);
                DisposeObject(deviceEnum);
            }

            return success;
        }

        public void StartMonitor()
        {
            mMustQuit = 0;
            rxThread = new Thread(new ThreadStart(ReceiveFunc));
            rxThread.Start();
        }

        public void StopMonitor()
        {
            mMustQuit = 1;
            rxThread.Abort();
        }

        void ReceiveFunc()
        {
            CanMessage canMessage;

            try
            {
                do
                {
                    // Wait 100 msec for a message reception
                    if (mRxEvent.WaitOne(100, false))
                    {
                        if (mReader != null)
                        {
                            // read a CAN message from the receive FIFO
                            while (mReader.ReadMessage(out canMessage))
                            {
                                AECanMessageDisplay newMessage = new AECanMessageDisplay(canMessage);
                                CANMsgReceived(newMessage);
                                if (canMessage.FrameType == CanMsgFrameType.Data)
                                {
                                    if ((canMessage.Identifier & SDOTransmitIdentifier) == SDOTransmitIdentifier)
                                    {
                                        AESDOMessage sdoMessage = new AESDOMessage(canMessage);
                                        if (SDOMsgReceived != null)
                                        {
                                            SDOMsgReceived(sdoMessage);
                                        }
                                    }
                                    else if (((canMessage.Identifier - TPDO1Identifier) >= 0) && ((canMessage.Identifier - TPDO1Identifier) <= MaxCANNodes))
                                    {
                                        if (PDOMsgReceived != null)
                                        {
                                            PDOMsgReceived(canMessage,(int)(canMessage.Identifier - TPDO1Identifier), 1);
                                        }
                                    }
                                    else if (((canMessage.Identifier - TPDO2Identifier) >= 0) && ((canMessage.Identifier - TPDO2Identifier) <= MaxCANNodes))
                                    {
                                        if (PDOMsgReceived != null)
                                        {
                                            PDOMsgReceived(canMessage, (int)(canMessage.Identifier - TPDO2Identifier), 2);
                                        }
                                    }
                                    else if (((canMessage.Identifier - TPDO3Identifier) >= 0) && ((canMessage.Identifier - TPDO3Identifier) <= MaxCANNodes))
                                    {
                                        if (PDOMsgReceived != null)
                                        {
                                            PDOMsgReceived(canMessage, (int)(canMessage.Identifier - TPDO3Identifier), 3);
                                        }
                                    }
                                    else if (((canMessage.Identifier - TPDO4Identifier) >= 0) && ((canMessage.Identifier - TPDO4Identifier) <= MaxCANNodes))
                                    {
                                        if (PDOMsgReceived != null)
                                        {
                                            PDOMsgReceived(canMessage, (int)(canMessage.Identifier - TPDO4Identifier), 4);
                                        }
                                    }
                                }
                            }
                        }
                    }
                } while (0 == mMustQuit);
            }
            catch (System.Threading.ThreadAbortException)
            {
                Console.WriteLine("Aborting receive thread.");
            }
        }

        public void SendSDO(uint nodeId,UInt16 mainIndex, byte subIndex, Int16 data)
        {
            CanMessage msg = new CanMessage();
            msg.Identifier = 0x600 + nodeId;
            msg.TimeStamp = 0;
            msg.FrameType = CanMsgFrameType.Data;
            msg.DataLength = 8;

            msg[0] = 0x2B;
            msg[1] = (byte)mainIndex;
            msg[2] = (byte)(mainIndex >> 8);
            msg[3] = subIndex;
            msg[4] = (byte)data;
            msg[5] = (byte)(data >> 8);
            msg[6] = 0;
            msg[7] = 0;

            mWriter.SendMessage(msg);
        }

        public void GetSDO(uint nodeId, UInt16 mainIndex, byte subIndex)
        {
            CanMessage msg = new CanMessage();
            msg.Identifier = 0x600 + nodeId;
            msg.TimeStamp = 0;
            msg.FrameType = CanMsgFrameType.Data;
            msg.DataLength = 8;

            msg[0] = 0x40;
            msg[1] = (byte)mainIndex;
            msg[2] = (byte)(mainIndex >> 8);
            msg[3] = subIndex;
            msg[4] = 0;
            msg[5] = 0;
            msg[6] = 0;
            msg[7] = 0;

            mWriter.SendMessage(msg);
        }

        public bool DestroyCANAdapter()
        {
            try
            {
                DisposeObject(mReader);
                DisposeObject(mWriter);
                DisposeObject(mCanChn);
                DisposeObject(mCanCtl);
                DisposeObject(mDevice);
                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }

        private void DisposeObject(object obj)
        {
            if (null != obj)
            {
                IDisposable dispose = obj as IDisposable;
                if (null != dispose)
                {
                    dispose.Dispose();
                    obj = null;
                }
            }
        }

        public UInt16 BusLoad
        {
            get { return (UInt16)mCanCtl.LineStatus.Busload; }
        }

        #endregion
    }
}
