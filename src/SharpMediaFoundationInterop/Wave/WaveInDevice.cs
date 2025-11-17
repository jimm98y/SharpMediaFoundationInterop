namespace SharpMediaFoundationInterop.Wave
{
    public class WaveInDevice
    {
        public uint DeviceID { get; }
        public uint Formats { get; }
        public string Name { get; }
        public ushort Channels { get; }
        public uint DriverVersion { get; }
        public ushort Mid { get; }
        public ushort Pid { get; }

        public WaveInDevice(uint deviceID, uint formats, string name, ushort channels, uint driverVersion, ushort mid, ushort pid)
        {
            DeviceID = deviceID;
            Formats = formats;
            Name = name;
            Channels = channels;
            DriverVersion = driverVersion;
            Mid = mid;
            Pid = pid;
        }
    }
}
