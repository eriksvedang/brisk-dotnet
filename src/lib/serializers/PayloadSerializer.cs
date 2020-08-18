namespace Piot.Brisk.Serializers
{
    using Brook;
    using Commands;

    public static class PayloadSerializer
    {
        public static void Serialize(IOutOctetStream stream, CustomConnectPayload payload)
        {
            stream.WriteUint8((byte)payload.Payload.Length);
            stream.WriteOctets(payload.Payload);
        }
    }
}