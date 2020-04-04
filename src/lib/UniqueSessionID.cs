namespace Piot.Brisk
{
    using Utils;

    public struct UniqueSessionID
    {
        public byte[] Value;

        public UniqueSessionID(byte[] value)
        {
            Value = value;
        }

        public override string ToString()
        {
            var hexString = HexConverter.ToString(Value, 128);
            return $"[unique session id {hexString}]";
        }
    }
}