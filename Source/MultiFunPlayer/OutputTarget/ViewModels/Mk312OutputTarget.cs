using Microsoft.Win32;
using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using RexLabsWifiShock;
using MK312WifiLibDotNet;

namespace MultiFunPlayer.OutputTarget.ViewModels;

[DisplayName("MK312")]
internal sealed class MK312OutputTarget(int instanceIndex, IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider, IInputProcessorManager inputManager)
    : ThreadAbstractOutputTarget(instanceIndex, eventAggregator, valueProvider)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private CancellationTokenSource _refreshCancellationSource = new();

    private MK312Device _mk312Device;

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy && !IsRefreshBusy && SelectedSerialPortDeviceId != null;

    public DeviceAxisUpdateType UpdateType { get; set; } = DeviceAxisUpdateType.FixedUpdate;
    public bool CanChangeUpdateType => !IsConnectBusy && !IsConnected;

    public ObservableConcurrentCollection<SerialPortInfo> SerialPorts { get; set; } = [];
    public SerialPortInfo SelectedSerialPort { get; set; }
    public string SelectedSerialPortDeviceId { get; set; }

    protected override IUpdateContext RegisterUpdateContext(DeviceAxisUpdateType updateType) => updateType switch
    {
        DeviceAxisUpdateType.FixedUpdate => new TCodeThreadFixedUpdateContext(),
        DeviceAxisUpdateType.PolledUpdate => new ThreadPolledUpdateContext(),
        _ => null,
    };

    protected override void OnInitialActivate()
    {
        base.OnInitialActivate();
        _ = RefreshPorts();
    }

    public bool CanChangePort => !IsRefreshBusy && !IsConnectBusy && !IsConnected;
    public bool IsRefreshBusy { get; set; }
    public bool CanRefreshPorts => !IsRefreshBusy && !IsConnectBusy && !IsConnected;

    private int _isRefreshingFlag;
    public async Task RefreshPorts()
    {
        if (Interlocked.CompareExchange(ref _isRefreshingFlag, 1, 0) != 0)
            return;

        try
        {
            var token = _refreshCancellationSource.Token;
            token.ThrowIfCancellationRequested();

            IsRefreshBusy = true;
            await DoRefreshPorts(token);
        }
        catch (Exception e)
        {
            Logger.Warn(e, $"{Identifier} port refresh failed with exception");
        }
        finally
        {
            Interlocked.Decrement(ref _isRefreshingFlag);
            IsRefreshBusy = false;
        }

        async Task DoRefreshPorts(CancellationToken token)
        {
            await Task.Delay(250, token);

            var serialPorts = new List<SerialPortInfo>();
            var scope = new ManagementScope("\\\\.\\ROOT\\cimv2");
            var observer = new ManagementOperationObserver();
            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("Win32_PnPEntity"));

            observer.ObjectReady += (_, e) =>
            {
                var portInfo = SerialPortInfo.FromManagementObject(e.NewObject as ManagementObject);
                if (portInfo == null)
                    return;

                serialPorts.Add(portInfo);
            };

            var taskCompletion = new TaskCompletionSource();
            observer.Completed += (_, _) => taskCompletion.TrySetResult();

            searcher.Get(observer);
            await using (token.Register(() => taskCompletion.TrySetCanceled()))
                await taskCompletion.Task.WaitAsync(token);

            var lastSelectedDeviceId = SelectedSerialPortDeviceId;
            SerialPorts.RemoveRange(SerialPorts.Except(serialPorts).ToList());
            SerialPorts.AddRange(serialPorts.Except(SerialPorts).ToList());

            SelectSerialPortByDeviceId(lastSelectedDeviceId);

            await Task.Delay(250, token);
        }
    }

    private void SelectSerialPortByDeviceId(string deviceId)
    {
        SelectedSerialPort = SerialPorts.FirstOrDefault(p => string.Equals(p.DeviceID, deviceId, StringComparison.Ordinal));
        if (SelectedSerialPort == null)
            SelectedSerialPortDeviceId = deviceId;
    }

    public void OnSelectedSerialPortChanged()  => SelectedSerialPortDeviceId = SelectedSerialPort?.DeviceID;

    protected override async ValueTask<bool> OnConnectingAsync()
    {
        if (SelectedSerialPortDeviceId == null)
            return false;
        if (SelectedSerialPort == null)
            await RefreshPorts();

        return SelectedSerialPort != null && await base.OnConnectingAsync();
    }

    protected override async ValueTask OnDisconnectingAsync()
    {
        if (_mk312Device != null)
        {
            if (_mk312Device.isConnected())
            {
                _mk312Device?.enableADC(false);
                _mk312Device?.disconnect();
            }
        }
        using var serialPort = CreateSerialPort();
        serialPort.Dispose();

        return;
    }


    protected override void Run(CancellationToken token)
    {
        try
        {
            // Inizializzazione della comunicazione seriale
            if (_mk312Device == null)
            {
                SerialComm serialComm = new SerialComm(SelectedSerialPort.PortName);
                _mk312Device = new MK312Device(serialComm, useEncryption: true, threadsafe: true);
                _mk312Device.connect();

            }
            else { _mk312Device.dryConnect(); }
            
            _mk312Device.initializeChannels();
            
            Status = ConnectionStatus.Connected;

            // Dizionari per tracciare i valori correnti e precedenti
            var currentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
            var lastSentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);

            while (!token.IsCancellationRequested)
            {
                // Popola il dizionario con i valori correnti degli assi
                GetValues(currentValues);

                // Seleziona gli assi desiderati
                var L0 = DeviceAxis.All[0];
                var L1 = DeviceAxis.All[1];
                var L2 = DeviceAxis.All[2];
                var R0 = DeviceAxis.All[3];
                var R1 = DeviceAxis.All[4];
                var R2 = DeviceAxis.All[5];


                // Invia i valori solo se sono cambiati (utilizzando IsValueDirty)
                if (DeviceAxis.IsValueDirty(currentValues[L0], lastSentValues[L0]))
                {
                    _mk312Device.setChannelALevel(currentValues[L0]);
                    lastSentValues[L0] = currentValues[L0];  // Aggiorna l'ultimo valore inviato
                }

                if (DeviceAxis.IsValueDirty(currentValues[R0], lastSentValues[R0]))
                {
                    _mk312Device.setChannelBLevel(currentValues[R0]);
                    lastSentValues[R0] = currentValues[R0];  // Aggiorna l'ultimo valore inviato
                }

                if (DeviceAxis.IsValueDirty(currentValues[L1], lastSentValues[L1]))
                {
                    _mk312Device.setChannel((uint)MK312Constants.RAM.ChannelAFrequency, currentValues[L1]);
                    lastSentValues[L1] = currentValues[L1];  // Aggiorna l'ultimo valore inviato
                }

                if (DeviceAxis.IsValueDirty(currentValues[R1], lastSentValues[R1]))
                {
                    _mk312Device.setChannel((uint)MK312Constants.RAM.ChannelBFrequency, currentValues[R1]);
                    lastSentValues[R1] = currentValues[R1];  // Aggiorna l'ultimo valore inviato
                }
                if (DeviceAxis.IsValueDirty(currentValues[L2], lastSentValues[L2]))
                {
                    _mk312Device.setChannel((uint)MK312Constants.RAM.ChannelAWidth, currentValues[L2]);
                    lastSentValues[L2] = currentValues[L2];  // Aggiorna l'ultimo valore inviato
                }

                if (DeviceAxis.IsValueDirty(currentValues[R2], lastSentValues[R2]))
                {
                    _mk312Device.setChannel((uint)MK312Constants.RAM.ChannelBWidth, currentValues[R2]);
                    lastSentValues[R2] = currentValues[R2];  // Aggiorna l'ultimo valore inviato
                }

                Task.Delay(10, token).Wait(token);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error in Run");
        }
    }


    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(UpdateType)] = JToken.FromObject(UpdateType);
            settings[nameof(SelectedSerialPort)] = SelectedSerialPortDeviceId;

        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<DeviceAxisUpdateType>(nameof(UpdateType), out var updateType))
                UpdateType = updateType;
            if (settings.TryGetValue<string>(nameof(SelectedSerialPort), out var selectedSerialPort))
                SelectSerialPortByDeviceId(selectedSerialPort);
        }
    }

    public override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region SerialPort
        s.RegisterAction<string>($"{Identifier}::SerialPort::Set", s => s.WithLabel("Device ID"), SelectSerialPortByDeviceId);
        #endregion
    }

    public override void UnregisterActions(IShortcutManager s)
    {
        base.UnregisterActions(s);
        s.UnregisterAction($"{Identifier}::SerialPort::Set");
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token)
    {
        try
        {
            await RefreshPorts();
            if (SelectedSerialPort == null)
                return false;

            using var serialPort = CreateSerialPort();
            serialPort.Open();
            serialPort.ReadExisting();
            serialPort.Close();

            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _refreshCancellationSource?.Cancel();
        _refreshCancellationSource?.Dispose();
        _refreshCancellationSource = null;

        base.Dispose(disposing);
    }

    private SerialPort CreateSerialPort() => new()
    {
        PortName = SelectedSerialPort.PortName,
    };

    public sealed class SerialPortInfo
    {
        private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        private SerialPortInfo() { }

        public string Caption { get; private set; }
        public string ClassGuid { get; private set; }
        public string Description { get; private set; }
        public string DeviceID { get; private set; }
        public string Manufacturer { get; private set; }
        public string Name { get; private set; }
        public string PNPClass { get; private set; }
        public string PNPDeviceID { get; private set; }
        public string PortName { get; private set; }

        public static SerialPortInfo FromManagementObject(ManagementObject o)
        {
            T GetPropertyValueOrDefault<T>(string propertyName, T defaultValue = default)
            {
                try { return (T)o.GetPropertyValue(propertyName); }
                catch { return defaultValue; }
            }

            try
            {
                var name = o.GetPropertyValue(nameof(Name)) as string;
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"\(COM\d+\)"))
                    return null;

                var deviceId = o.GetPropertyValue(nameof(DeviceID)) as string;
                var portName = Registry.GetValue($@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Enum\{deviceId}\Device Parameters", "PortName", "").ToString();

                return new SerialPortInfo()
                {
                    Caption = GetPropertyValueOrDefault<string>(nameof(Caption)),
                    ClassGuid = GetPropertyValueOrDefault<string>(nameof(ClassGuid)),
                    Description = GetPropertyValueOrDefault<string>(nameof(Description)),
                    Manufacturer = GetPropertyValueOrDefault<string>(nameof(Manufacturer)),
                    PNPClass = GetPropertyValueOrDefault<string>(nameof(PNPClass)),
                    PNPDeviceID = GetPropertyValueOrDefault<string>(nameof(PNPDeviceID)),
                    Name = name,
                    DeviceID = deviceId,
                    PortName = portName
                };
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to create SerialPortInfo [{0}]", o?.ToString());
            }

            return null;
        }

        public override bool Equals(object o) => o is SerialPortInfo other && string.Equals(DeviceID, other.DeviceID, StringComparison.Ordinal);
        public override int GetHashCode() => HashCode.Combine(DeviceID);
    }
}
