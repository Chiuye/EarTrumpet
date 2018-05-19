﻿using EarTrumpet.DataModel.Services;
using EarTrumpet.Extensions;
using EarTrumpet.Interop;
using EarTrumpet.Interop.MMDeviceAPI;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace EarTrumpet.DataModel.Internal
{
    class AudioDeviceManager : IMMNotificationClient, IAudioDeviceManager, IAudioDeviceManagerInternal
    {
        public event EventHandler<IAudioDevice> DefaultPlaybackDeviceChanged;
        public event EventHandler<IAudioDeviceSession> SessionCreated;

        public ObservableCollection<IAudioDevice> Devices => _devices;

        private IMMDeviceEnumerator _enumerator;
        private IAudioDevice _defaultPlaybackDevice;
        private IAudioDevice _defaultCommunicationsDevice;
        private ObservableCollection<IAudioDevice> _devices = new ObservableCollection<IAudioDevice>();
        private IVirtualDefaultAudioDevice _virtualDefaultDevice;
        private Dispatcher _dispatcher;

        public AudioDeviceManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);

            var devices = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE);
            uint deviceCount = devices.GetCount();
            for (uint i = 0; i < deviceCount; i++)
            {
                ((IMMNotificationClient)this).OnDeviceAdded(devices.Item(i).GetId());
            }

            // Trigger default logic to register for volume change
            QueryDefaultPlaybackDevice();
            QueryDefaultCommunicationsDevice();

            _virtualDefaultDevice = new VirtualDefaultAudioDevice(this);
        }

        private void QueryDefaultPlaybackDevice()
        {
            IMMDevice device = null;
            try
            {
                device = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                // Element not found.
            }

            string newDeviceId = device?.GetId();
            var currentDeviceId = _defaultPlaybackDevice?.Id;
            if (currentDeviceId != newDeviceId)
            {
                _defaultPlaybackDevice = (newDeviceId == null) ? null : FindDevice(newDeviceId);
                DefaultPlaybackDeviceChanged?.Invoke(this, _defaultPlaybackDevice);
            }
        }

        private void QueryDefaultCommunicationsDevice()
        {
            IMMDevice device = null;
            try
            {
                device = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eCommunications);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                // Element not found.
            }

            string newDeviceId = device?.GetId();
            var currentDeviceId = _defaultCommunicationsDevice?.Id;
            if (currentDeviceId != newDeviceId)
            {
                _defaultCommunicationsDevice = (newDeviceId == null) ? null : FindDevice(newDeviceId);
            }
        }

        public IVirtualDefaultAudioDevice VirtualDefaultDevice => _virtualDefaultDevice;

        public IAudioDevice DefaultPlaybackDevice
        {
            get => _defaultPlaybackDevice;
            set
            {
                if (_defaultPlaybackDevice == null ||
                    value.Id != _defaultPlaybackDevice.Id)
                {
                    DefaultEndPointService.SetDefaultDevice(value);
                }
            }
        }

        public IAudioDevice DefaultCommunicationDevice
        {
            get => _defaultCommunicationsDevice;
            set
            {
                if (_defaultCommunicationsDevice == null ||
                    value.Id != _defaultCommunicationsDevice.Id)
                {
                    DefaultEndPointService.SetDefaultDevice(value, ERole.eCommunications);
                }
            }
        }

        private bool HasDevice(string deviceId)
        {
            return _devices.Any(d => d.Id == deviceId);
        }

        private IAudioDevice FindDevice(string deviceId)
        {
            return _devices.First(d => d.Id == deviceId);
        }

        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                if (!HasDevice(pwstrDeviceId))
                {
                    try
                    {
                        IMMDevice device = _enumerator.GetDevice(pwstrDeviceId);
                        if (((IMMEndpoint)device).GetDataFlow() == EDataFlow.eRender)
                        {
                            _devices.Add(new SafeAudioDevice(new AudioDevice(device, this, _dispatcher)));
                        }
                    }
                    catch(COMException ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
            });
        }

        void IMMNotificationClient.OnDeviceRemoved(string pwstrDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                if (HasDevice(pwstrDeviceId))
                {
                    var device = FindDevice(pwstrDeviceId);
                    _devices.Remove(device);
                }
            });
        }

        void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            _dispatcher.SafeInvoke(() =>
            {
                QueryDefaultPlaybackDevice();
                QueryDefaultCommunicationsDevice();
            });
        }

        void IMMNotificationClient.OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState)
        {
            switch (dwNewState)
            {
                case DeviceState.ACTIVE:
                    ((IMMNotificationClient)this).OnDeviceAdded(pwstrDeviceId);
                    break;
                case DeviceState.DISABLED:
                case DeviceState.NOTPRESENT:
                case DeviceState.UNPLUGGED:
                    ((IMMNotificationClient)this).OnDeviceRemoved(pwstrDeviceId);
                    break;
                default:
                    Debug.WriteLine($"Unknown DEVICE_STATE: {dwNewState}");
                    break;
            }
        }

        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) { }

        public void OnSessionCreated(IAudioDeviceSession session)
        {
            SessionCreated?.Invoke(this, session);
        }
    }
}
