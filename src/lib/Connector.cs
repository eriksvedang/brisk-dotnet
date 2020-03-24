/*

MIT License

Copyright (c) 2017 Peter Bjorklund

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

namespace Piot.Brisk.Connect
{
    using System;
    using System.Net;
    using System.Text;
    using Flux.Client.Datagram;
    using Commands;
    using Serializers;
    using Time;
    using Brook;
    using Brook.Octet;
    using Tend.Client;
    using Log;
    using deserializers;
    using Stats.In;
    using Linger;
    using SimulationFrame;

    public enum ConnectionState
    {
        Challenge,
        TimeSync,
        Connected,
        Disconnected
    }

    public class Connector : IPacketReceiver
    {
        const byte NormalMode = 0x01;
        const byte OobMode = 0x02;

        ConnectionState state = ConnectionState.Challenge;
        Client udpClient;
        byte outSequenceNumber;
        uint challengeNonce;
        ushort connectionId;
        SequenceId lastIncomingSequence = SequenceId.Max;
        SequenceId outgoingSequenceNumber = SequenceId.Max;
        //Queue<byte[]> messageQueue = new Queue<byte[]>();
        IReceiveStream receiveStream;
        ISendStream sendStream;
        DateTime lastStateChange = DateTime.UtcNow;
        uint stateChangeWait = 500;
        MonotonicClockStopwatch monotonicStopwatch = new MonotonicClockStopwatch();
        IMonotonicClock monotonicClock;
        LatencyCollection latencies = new LatencyCollection();
        long localMillisecondsToRemoteMilliseconds;
        ILog log;
        DateTime lastValidHeader = DateTime.UtcNow;
        OutgoingLogic tendOut = new OutgoingLogic();
        IncomingLogic tendIn = new IncomingLogic();
        TimeStampedPacketHistory timestampedHistory = new TimeStampedPacketHistory();
        IInStatsCollector inStatsCollector = new InStatsCollector();
        IOutStatsCollector outStatsCollector = new OutStatsCollector();
        uint connectedPeriodInMs;
        long lastSentPackets;

        readonly bool useDebugLogging;
        private readonly UniqueSessionID sessionId;
        public UniqueSessionID SessionId => sessionId;

        public Connector(ILog log, IReceiveStream receiveStream, ISendStream sendStream, uint frequency)
        {
            this.receiveStream = receiveStream;
            this.sendStream = sendStream;
            this.log = log;
            if (frequency < 1)
            {
                frequency = 1;
            }
            if (frequency > 60)
            {
                frequency = 60;
            }
            connectedPeriodInMs = 1000 / frequency;
            useDebugLogging = false;
            monotonicClock = monotonicStopwatch;
            sessionId = RandomGenerator.RandomUniqueSessionId();
        }

        public void Dispose()
        {

        }

        public void Connect(string hostAndPort)
        {
            udpClient = new Client(hostAndPort, this);
            StartChallenge();
        }

        public long RemoteMonotonicMilliseconds
        {
            get
            {
                if (state != ConnectionState.Connected)
                {
                    throw new Exception("You can only check remote time if connected!");
                }
                return monotonicClock.NowMilliseconds() + localMillisecondsToRemoteMilliseconds;
            }
        }

        public AbsoluteSimulationFrame RemoteMonotonicSimulationFrame => ElapsedSimulationFrame.FromElapsedMilliseconds(RemoteMonotonicMilliseconds);


        public long ConnectedAt { get; private set; }

        static void WriteHeader(IOutOctetStream outStream, byte mode, byte sequence, ushort connectionIdToSend)
        {
            outStream.WriteUint8(mode);
            outStream.WriteUint8(sequence);
            outStream.WriteUint16(connectionIdToSend);
        }

        public InStats FetchInStats(int maxCount)
        {
            return inStatsCollector.GetInfo(maxCount);
        }

        public OutStats FetchOutStats(int maxCount)
        {
            return outStatsCollector.GetInfo(maxCount);
        }

        void RequestTime(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var timeSyncRequest = new TimeSyncRequest(now);

            TimeSyncRequestSerializer.Serialize(outStream, timeSyncRequest);
        }

        void SendPing(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var pingRequest = new PingRequestHeader(now);

            PingRequestHeaderSerializer.Serialize(outStream, pingRequest);
            TendSerializer.Serialize(outStream, tendIn, tendOut);
        }

        void SendChallenge(IOutOctetStream outStream)
        {
            if (useDebugLogging)
            {
                log.Debug("Sending Challenge!");
            }
            var challenge = new ChallengeRequest(challengeNonce, sessionId);

            ChallengeRequestSerializer.Serialize(outStream, challenge);
            lastStateChange = DateTime.UtcNow;
            stateChangeWait = 500;
        }

        public void SendTimeSync(IOutOctetStream outStream)
        {
            // log.Trace("Sending TimeSync!");

            RequestTime(outStream);
        }

        void SwitchState(ConnectionState newState, uint period)
        {
            if (useDebugLogging)
            {
                log.Debug($"State:{newState} period:{period}");
            }
            state = newState;
            stateChangeWait = period;
            lastStateChange = DateTime.UtcNow;
            if (state == ConnectionState.Connected)
            {
                receiveStream.OnTimeSynced();
            }
        }

        bool SendOneUpdatePacket(IOutOctetStream octetStream, out SequenceId sentSequenceId)
        {
            if (tendOut.CanIncrementOutgoingSequence)
            {
                outgoingSequenceNumber = outgoingSequenceNumber.Next();
                var tendSequenceId = tendOut.IncreaseOutgoingSequenceId();
                WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
                TendSerializer.Serialize(octetStream, tendIn, tendOut);
                var now = monotonicClock.NowMilliseconds();
                timestampedHistory.SentPacket(outgoingSequenceNumber, now, log);
                sentSequenceId = outgoingSequenceNumber;
                if (useDebugLogging)
                {
                    log.Trace($"SendOneUpdatePacket: tendSeq{tendSequenceId}");
                }
                return sendStream.Send(octetStream, tendSequenceId);
            }
            else
            {
                sentSequenceId = null;
                if (useDebugLogging)
                {
                    log.Trace("Can not send, forced to ping");
                }
                SendPing(octetStream);
                return true;
            }
        }

        void StartChallenge()
        {
            challengeNonce = RandomGenerator.RandomUInt();

        }

        bool SendOnePacket()
        {
            var isCompleteSend = true;
            var octetStream = new OutOctetStream();
            SequenceId sentSequenceNumber = null;

            switch (state)
            {
                case ConnectionState.Challenge:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
                    SendChallenge(octetStream);
                    break;
                case ConnectionState.TimeSync:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, connectionId);
                    SendTimeSync(octetStream);
                    break;
                case ConnectionState.Connected:
                    isCompleteSend = SendOneUpdatePacket(octetStream, out sentSequenceNumber);
                    break;
                case ConnectionState.Disconnected:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var octetsToSend = octetStream.Close();
            var now = monotonicClock.NowMilliseconds();
            if (sentSequenceNumber != null)
            {
                outStatsCollector.SequencePacketSent(sentSequenceNumber.Value, now, octetsToSend.Length);
            }
            else
            {
                outStatsCollector.PacketSent(now, octetsToSend.Length);
            }
            if (octetsToSend.Length > 0)
            {
                if (useDebugLogging)
                {
                    log.Trace($"Sending packet {ByteArrayToString(octetsToSend)}");
                }
                udpClient.Send(octetsToSend);
            }
            else
            {
                if (useDebugLogging)
                {
                    log.Trace($"Strange nothing to send");
                }

            }
            return isCompleteSend;
        }

        void CheckDisconnect()
        {
            if (state == ConnectionState.Challenge)
            {
                return;
            }
            var timeSinceReceivedHeader = DateTime.UtcNow - lastValidHeader;
            const int DisconnectTime = 3;

            if (timeSinceReceivedHeader.Seconds > DisconnectTime)
            {
                if (useDebugLogging)
                {
                    log.Debug($"Disconnect detected! #{timeSinceReceivedHeader.Seconds}");
                }
                SwitchState(ConnectionState.Disconnected, 9999);
                receiveStream.Lost();
            }
        }

        private void SendPackets()
        {
            var now = monotonicClock.NowMilliseconds();

            var diff = now - lastSentPackets;
            if (diff < 50)
            {
                return;
            }
            lastSentPackets = now;

            const int burstCount = 3;
            for (var i = 0; i < burstCount; ++i)
            {
                var isDone = SendOnePacket();
                if (isDone)
                {
                    break;
                }
            }
        }

        public void Update()
        {
            var diff = DateTime.UtcNow - lastStateChange;
            if (diff.TotalMilliseconds < stateChangeWait)
            {
                return;
            }

            CheckDisconnect();
            SendPackets();

            if (state == ConnectionState.Connected)
            {
                stateChangeWait = connectedPeriodInMs;
                lastStateChange = DateTime.UtcNow;
            }
        }

        static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);

            foreach (byte b in ba)
            {
                hex.AppendFormat("{0:x2}", b);
            }
            return hex.ToString();
        }

        void OnChallengeResponse(IInOctetStream stream)
        {
            if (state != ConnectionState.Challenge)
            {
                return;
            }
            var nonce = stream.ReadUint32();
            var assignedConnectionId = stream.ReadUint16();

            if (useDebugLogging)
            {
                log.Debug($"Challenge response {nonce:X} {assignedConnectionId:X}");
            }

            if (nonce == challengeNonce)
            {
                if (useDebugLogging)
                {
                    log.Debug($"We have a connection! {assignedConnectionId}");
                }
                connectionId = assignedConnectionId;
                SwitchState(ConnectionState.TimeSync, connectedPeriodInMs);
            }
        }

        void OnPongResponseCmd(PongResponseHeader response)
        {
        }

        void OnPongResponse(IInOctetStream stream, long packetTime)
        {
            var pongResponse = PongResponseHeaderDeserializer.Deserialize(stream);

            OnPongResponseCmd(pongResponse);
            HandleTend(stream, packetTime);
        }

        void OnTimeSyncResponse(IInOctetStream stream)
        {
            //log.Trace("On Time Sync response");

            if (state != ConnectionState.TimeSync)
            {
                if (useDebugLogging)
                {
                    log.Warning("We are not in timesync state anymore");
                }
                return;
            }
            var echoedTicks = stream.ReadUint64();
            var latency = monotonicClock.NowMilliseconds() - (long)echoedTicks;
            // log.Trace($"Latency: {latency}");
            var remoteTicks = stream.ReadUint64();
            latencies.AddLatency((ushort)latency);
            ushort averageLatency;
            var isStable = latencies.StableLatency(out averageLatency);

            if (isStable)
            {
                var remoteTimeIsNow = remoteTicks + averageLatency / (ulong)2;
                //log.Trace($"We are stable! latency:{averageLatency}");
                localMillisecondsToRemoteMilliseconds = (long)remoteTimeIsNow - monotonicClock.NowMilliseconds();
                latencies = new LatencyCollection();
                ConnectedAt = monotonicClock.NowMilliseconds();
                SwitchState(ConnectionState.Connected, 100);
            }
            else
            {
                //log.Trace("Not stable yet, keep sending");
            }
        }

        void ReadOOB(IInOctetStream stream, long packetTime)
        {
            var cmd = stream.ReadUint8();

            switch (cmd)
            {
                case CommandValues.ChallengeResponse:
                    OnChallengeResponse(stream);
                    break;
                case CommandValues.TimeSyncResponse:
                    OnTimeSyncResponse(stream);
                    break;
                case CommandValues.PongResponse:
                    OnPongResponse(stream, packetTime);
                    break;
                default:
                    throw new Exception($"Unknown command {cmd}");
            }
        }

        void CallbackTend()
        {
            while (tendOut.Count > 0)
            {
                var deliveryInfo = tendOut.Dequeue();
                if (useDebugLogging)
                {
                    log.Debug($"we have deliveryInfo {deliveryInfo}");
                }
                receiveStream.PacketDelivery(deliveryInfo.PacketSequenceId, deliveryInfo.WasDelivered);
                outStatsCollector.UpdateConfirmation(deliveryInfo.PacketSequenceId.Value, deliveryInfo.WasDelivered);
            }
        }

        SequenceId HandleTend(IInOctetStream stream, long packetTime)
        {
            var info = TendDeserializer.Deserialize(stream);
            if (useDebugLogging)
            {
                log.Debug($"received tend {info} ");
            }


            tendIn.ReceivedToUs(info.PacketId);
            var valid = tendOut.ReceivedByRemote(info.Header);
            if (valid)
            {
                PacketHistoryItem foundItem;
                var worked = timestampedHistory.ReceivedConfirmation(info.Header.SequenceId, out foundItem, log);
                if (worked)
                {
                    var latency = packetTime - foundItem.Time;
                    var foundPacket = outStatsCollector.UpdateLatency((uint)info.Header.SequenceId.Value, latency);
                    if (!foundPacket)
                    {
                        log.Warning("didnt find latency to update", "sequenceID", info.Header.SequenceId.Value);
                    }
                }
            }
            CallbackTend();

            return info.PacketId;
        }

        void ReadConnectionPacket(IInOctetStream stream, long nowMs)
        {
            var packetId = HandleTend(stream, nowMs);

            receiveStream.Receive(stream, packetId);
        }

        void ReadHeader(IInOctetStream stream, byte mode, int packetOctetCount, long nowMs)
        {
            var sequence = stream.ReadUint8();
            var assignedConnectionId = stream.ReadUint16();

            if (assignedConnectionId == 0)
            {
                ReadOOB(stream, nowMs);
            }
            else
            {
                if (assignedConnectionId == connectionId)
                {
                    var headerSequenceId = new SequenceId(sequence);

                    if (lastIncomingSequence.IsValidSuccessor(headerSequenceId))
                    {
                        var diff = lastIncomingSequence.Distance(headerSequenceId);
                        var timeStamp = DateTime.UtcNow;
                        if (diff > 1)
                        {
                            inStatsCollector.PacketsDropped(timeStamp, diff - 1);
                        }
                        inStatsCollector.PacketReceived(timeStamp, packetOctetCount);

                        lastIncomingSequence = headerSequenceId;

                        if (mode == OobMode)
                        {
                            ReadOOB(stream, nowMs);
                        }
                        else
                        {
                            ReadConnectionPacket(stream, nowMs);
                        }
                    }
                }
            }

            lastValidHeader = DateTime.UtcNow;
            if (useDebugLogging)
            {
                log.Info($"Marked valid header...{lastValidHeader}");
            }
        }

        void IPacketReceiver.ReceivePacket(byte[] octets, IPEndPoint fromEndpoint)
        {
            if (octets.Length < 4)
            {
                return;
            }
            var nowMs = monotonicClock.NowMilliseconds();
            var packetOctetCount = octets.Length;
            var stream = new InOctetStream(octets);
            var mode = stream.ReadUint8();
            if (useDebugLogging)
            {
                log.Trace($"received packet:{octets.Length} mode:{mode}");
            }
            switch (mode)
            {
                case NormalMode:
                    ReadHeader(stream, mode, packetOctetCount, nowMs);
                    break;
                case OobMode:
                    ReadHeader(stream, mode, packetOctetCount, nowMs);
                    break;

                default:
                    throw new Exception($"Unknown mode {mode}");
            }
        }

        void IPacketReceiver.HandleException(Exception e)
        {
            receiveStream.HandleException(e);
        }
        public ConnectionState ConnectionState
        {
            get
            {
                return state;
            }
        }
    }
}
