namespace Piot.Brisk.Serializers
{
    using Brook;

    public static class UniqueSessionIdSerializer
    {
        public static void Serialize(IOutOctetStream stream, UniqueSessionID sessionId)
        {
            stream.WriteUint8((byte)sessionId.Value.Length);
            stream.WriteOctets(sessionId.Value);
        }
    }
}