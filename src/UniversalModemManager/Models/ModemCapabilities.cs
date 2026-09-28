namespace UniversalModemManager.Models;

[Flags]
public enum ModemCapability
{
    None = 0,
    Identity = 1 << 0,
    Signal = 1 << 1,
    NetworkStatus = 1 << 2,
    Wifi = 1 << 3,
    WifiClients = 1 << 4,
    ClientDisconnect = 1 << 5,
    MacFiltering = 1 << 6,
    Traffic = 1 << 7,
    Sms = 1 << 8,
    Ussd = 1 << 9,
    Apn = 1 << 10,
    NetworkMode = 1 << 11,
    BandControl = 1 << 12,
    Reboot = 1 << 13,
    Battery = 1 << 14
}
