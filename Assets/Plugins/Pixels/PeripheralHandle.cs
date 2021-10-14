
namespace Systemic.Pixels.Unity.BluetoothLE
{
    // Readonly struct
    public struct PeripheralHandle
    {
        public interface INativePeripheral { }

        public PeripheralHandle(INativePeripheral client) => SystemClient = client;

        public INativePeripheral SystemClient { get; }

        public bool IsValid => SystemClient != null;
    }
}
