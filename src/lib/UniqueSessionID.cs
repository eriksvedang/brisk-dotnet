namespace Piot.Brisk
{
    using System;

    public struct UniqueSessionID
    {
        public byte[] Value;

        public UniqueSessionID(byte[] value)
        {
            Value = value;
        }

        public override string ToString()
        {
            var hexString = BitConverter.ToString(Value).Replace("-", string.Empty);
            return $"[unique session id {hexString}]";
        }
    }
}