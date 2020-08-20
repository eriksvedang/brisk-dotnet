namespace Piot.Brisk.deserializers
{
    using Piot.Brisk.Commands;
    using Piot.Brook;

    public static class PayloadDeserializer
    {
        public static CustomConnectPayload Deserialize(IInOctetStream stream)
        {
            var octetCount = stream.ReadUint8();
            var octets = stream.ReadOctets(octetCount);

            return new CustomConnectPayload { Payload = octets };
        }
    }
}