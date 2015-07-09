using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AE.CANOPEN;
using Xceed.Wpf.Toolkit;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Codeplex.Dashboarding;

namespace CrashCAN
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 

    enum ChargeState { Charging,Discharging};

    public partial class MainWindow : Window
    {
        IxxatCANOpenUSB mCanController = null;
        ObservableCollection<AECanMessageDisplay> msgQueue = null;
        AEViewModel viewData = null;
        Timer socTimer = null;
        TimerCallback socTimerCallback = null;

        int mMsgCount = 0;
        const double BMMVoltageScaleFactor = .025;
        const double DSPVoltageScaleFactor = .0358587014572;
        const double DSPCurrentScaleFactor = .047607421875;
        const double DSPFrequencyScaleFactor = .00305252561351;
        const double DSPTemperatureScaleFactor = .01;
        const int mMaxRcvQueueLength = 500;
        const int DSPNodeId = 1;
        const int MBMMNodeId = 42;
        const int socTimerInterval = 2000;   //2 sec

        static ChargeState chargeState = ChargeState.Discharging;

        public MainWindow()
        {
            InitializeComponent();
            mCanController = new IxxatCANOpenUSB();
            mCanController.CANMsgReceived += new CANMsgReceivedEventHandler(mCanController_CANMsgReceived);
            mCanController.SDOMsgReceived += new SDOMsgReceivedEventHandler(mCanController_SDOMsgReceived);
            mCanController.PDOMsgReceived += new PDOMsgReceivedEventHandler(mCanController_PDOMsgReceived);

            msgQueue = new ObservableCollection<AECanMessageDisplay>();

            rcvMessageList.ItemsSource = msgQueue;
            viewData = new AEViewModel();
            this.DataContext = viewData;
            viewData.SOCAvailable = 100;

            setPointsGroupBox.IsEnabled = false;

            socTimerCallback = new TimerCallback(ChargeCycleTask);

        }

        void mCanController_PDOMsgReceived(Ixxat.Vci3.Bal.Can.CanMessage msg,int nodeId, int pdoNumber)
        {
            if (nodeId == MBMMNodeId)
            {
                switch (pdoNumber)
                {
                    case 1:
                        viewData.IMRCont = (Int16)((msg[1] << 8) + msg[0]);
                        viewData.IMD = (Int16)((msg[3] << 8) + msg[2]);
                        viewData.IMR = (Int16)((msg[5] << 8) + msg[4]);
                        break;
                    case 2:
                        break;
                    case 3:
                        viewData.SOCAvailable = (Int16)msg[1];
                        viewData.SOCConnected = (Int16)msg[2];
                        viewData.BMMConnected = (Int16)msg[3];
                        viewData.MaxT = (Int16)msg[5];
                        break;
                    case 4:
                        viewData.VMD = (Int16)((double)((msg[1] << 8) + msg[0]) * BMMVoltageScaleFactor);
                        viewData.VMR = (Int16)((double)((msg[3] << 8) + msg[2]) * BMMVoltageScaleFactor);
                        viewData.ActualV = (Int16)((double)((msg[5] << 8) + msg[4]) * BMMVoltageScaleFactor);
                        viewData.ActualI = (Int16)((msg[7] << 8) + msg[6]);
                        break;

                    default:
                        break;
                }
            }
            else if(nodeId == DSPNodeId)
            {
                switch (pdoNumber)
                {
                    case 3:
                        viewData.InverterState = (UInt16)((msg[1] << 8) + msg[0]);
                        viewData.GridFrequency = ((msg[3] << 8) + msg[2]) * DSPFrequencyScaleFactor;
                        viewData.PhaseVoltage = ((msg[5] << 8) + msg[4]) * DSPVoltageScaleFactor; 
                        viewData.PhaseCurrent = (UInt16)((msg[7] << 8) + msg[6]) * DSPCurrentScaleFactor;
                        break;
                    case 4:
                        viewData.RealPower = (Int16)((msg[1] << 8) + msg[0]);
                        viewData.ImagPower = (Int16)((msg[3] << 8) + msg[2]);
                        viewData.SwitchTemperature = ((msg[5] << 8) + msg[4]) * DSPTemperatureScaleFactor;
                        viewData.FaultWord = (UInt16)((msg[7] << 8) + msg[6]);

                        break;
                }
            }

            viewData.BusLoad = mCanController.BusLoad;
        }

        void mCanController_SDOMsgReceived(AESDOMessage msg)
        {
            //Update view model
            if (msg.NodeId == DSPNodeId)
            {
                switch (msg.MajorIdentifier)
                {
                    case AESXCANObjects.KWCommand:
                        viewData.KwCommand = (Int16)msg.Data;
                        break;
                        
                    case AESXCANObjects.KWRampRate:
                        viewData.KwRampRate = (Int16)msg.Data;
                        break;

                    case AESXCANObjects.VarCommand:
                        viewData.VarCommand = (Int16)msg.Data;
                        break;

                    case AESXCANObjects.VarRampRate:
                        viewData.VarRampRate = (Int16)msg.Data;
                        break;

                    default:
                        break;
                }
            }

        }


        void mCanController_CANMsgReceived(AECanMessageDisplay msg)
        {
            IncrementMsgCount();
            UpdateReceivedMsgQueue(msg);
        }


        void IncrementMsgCount()
        {
            Dispatcher.Invoke(DispatcherPriority.Input, new ThreadStart(() =>
                {
                    mMsgCount++;
                    statusBarText.Text = String.Format("{0} messages received", mMsgCount);
                }));
        }

        void UpdateReceivedMsgQueue(AECanMessageDisplay msg)
        {
            Dispatcher.Invoke(DispatcherPriority.Input, new ThreadStart(() =>
            {
                if (msgQueue.Count > mMaxRcvQueueLength)
                {
                    msgQueue.Clear();
                }
                msgQueue.Add(msg);
                rcvMessageList.ScrollIntoView(rcvMessageList.Items[rcvMessageList.Items.Count - 1]);
            }));

        }


        private void connectButton_Click(object sender, RoutedEventArgs e)
        {
            if (connectedRadio.IsChecked == false)
            {
                if (mCanController.InitCANAdapter())
                {
                    ToggleConnectButtonText();
                    connectedRadio.IsChecked = true;
                    setPointsGroupBox.IsEnabled = true;
                    mCanController.StartMonitor();
                    GetSetpoints();
                }
                else
                {
                    System.Windows.MessageBox.Show("Unable to find CAN adapter");
                }
            }
            else
            {
                setPointsGroupBox.IsEnabled = false;
                mCanController.StopMonitor();
                if (mCanController.DestroyCANAdapter())
                {
                    ToggleConnectButtonText();
                    connectedRadio.IsChecked = false;
                    setPointsGroupBox.IsEnabled = false;
                }
            }
        }

        private void ToggleConnectButtonText()
        {
            if (connectButton.Content.ToString() == "Connect")
            {
                connectButton.Content = "Disconnect";
            }
            else
            {
                connectButton.Content = "Connect";
            }
        }

        private void enableButton_Click(object sender, RoutedEventArgs e)
        {
            if (enableButton.Content.ToString() == "Enable ESS")
            {
                enableButton.Content = "Disable ESS";
                EnableEss();
            }
            else
            {
                enableButton.Content = "Enable ESS";
                EnableEss(false);
            }
        }

        private void EnableEss(bool enable = true)
        {
            if (enable)
            {
                mCanController.SendSDO(DSPNodeId, AESXCANObjects.EnableESS, 0, 1);
            }
            else
            {
                mCanController.SendSDO(DSPNodeId, AESXCANObjects.EnableESS, 0, 0);
            }
        }

        private void EnableChargeCycle(bool enable = true)
        {
            if(enable == true)
            {
                socTimer = new Timer(socTimerCallback, new object(), socTimerInterval, socTimerInterval);
            }
            else
            {
                socTimer.Dispose();
            }
        }

        private void ChargeCycleTask(object obj)
        {
            
            if( chargeState == ChargeState.Discharging )
            {
                if( viewData.SOCConnected <= viewData.MinSOC )
                {
                    mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWCommand, 0, viewData.ChargeRate);
                    chargeState = ChargeState.Charging;
                }
                else
                {
                    mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWCommand, 0, viewData.DischargeRate);
                    chargeState = ChargeState.Discharging;
                }
            }
            else if( chargeState == ChargeState.Charging )
            {
                if( viewData.SOCConnected >= viewData.MaxSOC )
                {
                    mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWCommand, 0, viewData.DischargeRate);
                    chargeState = ChargeState.Discharging;
                }
                else
                {
                    mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWCommand, 0, viewData.ChargeRate);
                    chargeState = ChargeState.Charging;
                }
            }
        }

        private void updateSetpointsButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSetpoints();
            GetSetpoints();
        }

        private void UpdateSetpoints()
        {
            mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWCommand, 0, viewData.KwCommand);
            mCanController.SendSDO(DSPNodeId, AESXCANObjects.KWRampRate, 0, viewData.KwRampRate);
            mCanController.SendSDO(DSPNodeId, AESXCANObjects.VarCommand, 0, viewData.VarCommand);
            mCanController.SendSDO(DSPNodeId, AESXCANObjects.VarRampRate, 0, viewData.VarRampRate);

        }

        private void GetSetpoints()
        {
            mCanController.GetSDO(DSPNodeId, AESXCANObjects.KWCommand, 0);
            mCanController.GetSDO(DSPNodeId, AESXCANObjects.KWRampRate, 0);
            mCanController.GetSDO(DSPNodeId, AESXCANObjects.VarCommand, 0);
            mCanController.GetSDO(DSPNodeId, AESXCANObjects.VarRampRate, 0);
        }

        private void enableChargeCycleButton_Click(object sender, RoutedEventArgs e)
        {
            if (enableChargeCycleButton.Content.ToString() == "Enable")
            {
                enableChargeCycleButton.Content = "Disable";
                kwCommand.IsEnabled = false;
                EnableChargeCycle();
            }
            else
            {
                enableChargeCycleButton.Content = "Enable";
                kwCommand.IsEnabled = true;
                EnableChargeCycle(false);
            }
        }
    }

    public class AEViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private Int16 kwCommand, kwRampRate, varCommand, varRampRate;
        private Int16 imrCont, imr, imd, socAvailable, socConnected, bmmConnected, maxT, vmd, vmr, actualV, actualI,realPower,imagPower,minSOC,maxSOC,chargeRate,dischargeRate;
        private UInt16 inverterState, faultWord,busLoad;
        private Double gridFrequency, phaseVoltage, phaseCurrent, switchTemperature;
        private Dictionary<UInt16, String> stateDictionary;
        private String stateString;

        public AEViewModel()
        {
            kwCommand = kwRampRate = varCommand = varRampRate = 0;
            minSOC = maxSOC = chargeRate = dischargeRate = 0;
            InitStateDictionary();

        }

        private void InitStateDictionary()
        {
            stateDictionary = new Dictionary<ushort, string>();
            stateDictionary.Add(0, "Sleeping");
            stateDictionary.Add(1, "Startup Delay");
            stateDictionary.Add(2, "AC Precharge");
            stateDictionary.Add(3, "DC Precharge");
            stateDictionary.Add(4, "Idle");
            stateDictionary.Add(5, "Power tracking");
            stateDictionary.Add(6, "Constant current");
            stateDictionary.Add(7, "Constant voltage");
            stateDictionary.Add(8, "Array mode");
            stateDictionary.Add(9, "Fault");
            stateDictionary.Add(10, "Initializing");
            stateDictionary.Add(11, "Disabled");
            stateDictionary.Add(12, "Latched fault");
            stateDictionary.Add(13, "Cooldown");
        }

        private void NotifyPropertyChanged(String info)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(info));
            }
        }

        public Int16 KwCommand
        {
            get
            {
                return this.kwCommand;
            }
            set
            {
                this.kwCommand = value;
                NotifyPropertyChanged("KwCommand");

            }
        }

        public Int16 KwRampRate
        {
            get
            {
                return this.kwRampRate;
            }
            set
            {
                this.kwRampRate = value;
                NotifyPropertyChanged("KwRampRate");

            }
        }

        public Int16 VarCommand
        {
            get
            {
                return this.varCommand;
            }
            set
            {
                this.varCommand = value;
                NotifyPropertyChanged("VarCommand");

            }
        }

        public Int16 VarRampRate
        {
            get
            {
                return this.varRampRate;
            }
            set
            {
                this.varRampRate = value;
                NotifyPropertyChanged("VarRampRate");

            }
        }

        public Int16 IMRCont
        {
            get
            {
                return this.imrCont;
            }
            set
            {
                this.imrCont = value;
                NotifyPropertyChanged("IMRCont");

            }
        }

        public Int16 IMR
        {
            get
            {
                return this.imr;
            }
            set
            {
                this.imr = value;
                NotifyPropertyChanged("IMR");

            }
        }

        public Int16 IMD
        {
            get
            {
                return this.imd;
            }
            set
            {
                this.imd = value;
                NotifyPropertyChanged("IMD");

            }
        }

        public Int16 SOCAvailable
        {
            get
            {
                return this.socAvailable;
            }
            set
            {
                this.socAvailable = value;
                NotifyPropertyChanged("SOCAvailable");

            }
        }

        public Int16 SOCConnected
        {
            get
            {
                return this.socConnected;
            }
            set
            {
                this.socConnected = value;
                NotifyPropertyChanged("SOCConnected");

            }
        }

        public Int16 BMMConnected
        {
            get
            {
                return this.bmmConnected;
            }
            set
            {
                this.bmmConnected = value;
                NotifyPropertyChanged("BMMConnected");

            }
        }

        public Int16 MaxT
        {
            get
            {
                return this.maxT;
            }
            set
            {
                this.maxT = value;
                NotifyPropertyChanged("MaxT");

            }
        }

        public Int16 VMD
        {
            get
            {
                return this.vmd;
            }
            set
            {
                this.vmd = value;
                NotifyPropertyChanged("VMD");

            }
        }

        public Int16 VMR
        {
            get
            {
                return this.vmr;
            }
            set
            {
                this.vmr = value;
                NotifyPropertyChanged("VMR");

            }
        }

        public Int16 ActualV
        {
            get
            {
                return this.actualV;
            }
            set
            {
                this.actualV = value;
                NotifyPropertyChanged("ActualV");

            }
        }

        public Int16 ActualI
        {
            get
            {
                return this.actualI;
            }
            set
            {
                this.actualI = value;
                NotifyPropertyChanged("ActualI");

            }
        }

        public UInt16 InverterState
        {
            get
            {
                return this.inverterState;
            }
            set
            {
                this.inverterState = value;
                NotifyPropertyChanged("InverterState");
                NotifyPropertyChanged("InverterStateString");

            }
        }

        public String InverterStateString
        {
            get
            {
                stateDictionary.TryGetValue(this.InverterState, out stateString);
                return stateString;
            }
            set
            {
                this.stateString = value;
                NotifyPropertyChanged("InverterStateString");
            }
        }

        public Double GridFrequency
        {
            get
            {
                return this.gridFrequency;
            }
            set
            {
                this.gridFrequency = value;
                NotifyPropertyChanged("GridFrequency");

            }
        }

        public Double PhaseVoltage
        {
            get
            {
                return this.phaseVoltage;
            }
            set
            {
                this.phaseVoltage = value;
                NotifyPropertyChanged("PhaseVoltage");

            }
        }

        public Double PhaseCurrent
        {
            get
            {
                return this.phaseCurrent;
            }
            set
            {
                this.phaseCurrent = value;
                NotifyPropertyChanged("PhaseCurrent");

            }
        }

        public Int16 RealPower
        {
            get
            {
                return this.realPower;
            }
            set
            {
                this.realPower = value;
                NotifyPropertyChanged("RealPower");

            }
        }

        public Int16 ImagPower
        {
            get
            {
                return this.imagPower;
            }
            set
            {
                this.imagPower = value;
                NotifyPropertyChanged("ImagPower");

            }
        }

        public Double SwitchTemperature
        {
            get
            {
                return this.switchTemperature;
            }
            set
            {
                this.switchTemperature = value;
                NotifyPropertyChanged("SwitchTemperature");

            }
        }

        public UInt16 FaultWord
        {
            get
            {
                return this.faultWord;
            }
            set
            {
                this.faultWord = value;
                NotifyPropertyChanged("FaultWord");

            }
        }

        public UInt16 BusLoad
        {
            get { return this.busLoad; }
            set 
            {
                this.busLoad = value;
                NotifyPropertyChanged("BusLoad");
            }
        }

        public Int16 MinSOC
        {
            get { return this.minSOC; }
            set
            {
                this.minSOC = value;
                NotifyPropertyChanged("MinSOC");
            }
        }

        public Int16 MaxSOC
        {
            get { return this.maxSOC; }
            set
            {
                this.maxSOC = value;
                NotifyPropertyChanged("MaxSOC");
            }
        }

        public Int16 ChargeRate
        {
            get { return this.chargeRate; }
            set
            {
                this.chargeRate = value;
                NotifyPropertyChanged("ChargeRate");
            }
        }

        public Int16 DischargeRate
        {
            get { return this.dischargeRate; }
            set
            {
                this.dischargeRate = value;
                NotifyPropertyChanged("DischargeRate");
            }
        }





        
    }
}
