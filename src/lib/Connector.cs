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

using System.Net.Sockets;

namespace Piot.Brisk.Connect
{
    using System.Diagnostics;
    using System.Net;
    using System.Text;
    using System;
    using Brook.Octet;
    using Brook;
    using Commands;
    using deserializers;
    using Flux.Client.Datagram;
    using Linger;
    using Log;
    using Serializers;
    using SimulationFrame;
    using Stats.In;
    using Stats;
    using Tend.Client;
    using Time;

    public enum ConnectionState
    {
        Idle,
        Challenge,
        TimeSync,
        ConnectRequest,
        Connected,
        DisconnectRequest,
        Disconnected
    }

    public class Connector : IPacketReceiver
    {
        const byte NormalMode = 0x01;
        const byte OobMode = 0x02;
        const int MaxChallengeAttempts = 3;
        const int MinExpectedPacketLength = 4;

        ConnectionState state = ConnectionState.Idle;
        Client udpClient;
        byte outSequenceNumber;
        uint challengeNonce;
        ushort connectionId;
        SequenceId lastIncomingSequence = SequenceId.Max;

        SequenceId outgoingSequenceNumber = SequenceId.Max;

        long lastStateChange;
        uint stateChangeWait = 500;
        MonotonicClockStopwatch monotonicStopwatch = new MonotonicClockStopwatch();
        IMonotonicClock monotonicClock;
        LatencyCollection latencies = new LatencyCollection();
        long localMillisecondsToRemoteMilliseconds;
        ILog log;
        long lastValidHeader;
        OutgoingLogic tendOut = new OutgoingLogic();
        IncomingLogic tendIn = new IncomingLogic();
        TimeStampedPacketHistory timestampedHistory = new TimeStampedPacketHistory();
        IInStatsCollector inStatsCollector = new InStatsCollector();
        IOutStatsCollector outStatsCollector = new OutStatsCollector();
        uint connectedPeriodInMs;
        long lastSentPackets;
        private IOutOctetStream pendingOutOctetStream;
        private SequenceId pendingOutSequenceNumber;
        private bool pendingOutSequenceNumberUsed;

        private readonly UniqueSessionID sessionId;
        public UniqueSessionID SessionId => sessionId;
        private readonly PacketBuffer incomingPacketBuffer;
        private readonly PacketReceivedNotifications receivedNotifications = new PacketReceivedNotifications();
        private string lastHost;

        private SimpleStatsCollector simpleIn = new SimpleStatsCollector();
        private SimpleStatsCollector simpleOut = new SimpleStatsCollector();
        private SimpleStats simpleInStats;
        private SimpleStats simpleOutStats;
        private long lastStatsUpdate;

        private int remainingChallengeAttempts = 0;

        public float disconnectTimeout = 3;

        public bool syncTimeOnConnect;

        private uint remoteNonce;

        private ConnectInfo connectInfo;

        public Connector(ILog log, uint frequency, IPort port)
        {
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
            monotonicClock = monotonicStopwatch;
            incomingPacketBuffer = new PacketBuffer(monotonicClock);
            sessionId = RandomGenerator.RandomUniqueSessionId();
            udpClient = new Client(this, port);
            Reset();
        }

        private void Reset()
        {
            state = ConnectionState.Idle;
            lastIncomingSequence = SequenceId.Max;
            outgoingSequenceNumber = SequenceId.Max;
            pendingOutSequenceNumber = outgoingSequenceNumber;
            outSequenceNumber = 0;
            challengeNonce = 0;
            connectInfo = new ConnectInfo();
            tendIn.Clear();
            tendOut.Clear();
            incomingPacketBuffer.Clear();
            receivedNotifications.Clear();
            lastStateChange = monotonicClock.NowMilliseconds();
            lastValidHeader = monotonicClock.NowMilliseconds();
            lastSentPackets = monotonicClock.NowMilliseconds();
            remainingChallengeAttempts = MaxChallengeAttempts;
        }

        public bool MustSendPacket => TimeHasPassedSinceLastPacket;

        public void Dispose()
        {
            Disconnect();
        }

        public void Connect(string hostAndPort, ConnectInfo connectInfo)
        {
            if (hostAndPort == "")
            {
                return;
            }

            udpClient.Start();

            if (lastHost != hostAndPort)
            {
                udpClient.SetDefaultSendEndpoint(hostAndPort);
                lastHost = hostAndPort;
            }

            Reset();
            this.connectInfo = connectInfo;
            StartChallenge();
        }

        public void Disconnect()
        {
            SwitchState(ConnectionState.DisconnectRequest, 0);
            PreparePacket();
            SendPreparedPacket();
            udpClient.Stop();
            ReceivedNotifications.Clear();
            IncomingPackets.Clear();
            SwitchState(ConnectionState.Disconnected, 0);
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

        public AbsoluteSimulationFrame RemoteMonotonicSimulationFrame =>
            ElapsedSimulationFrame.FromElapsedMilliseconds(RemoteMonotonicMilliseconds);

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

        public SimpleStats FetchSimpleInStats()
        {
            return simpleInStats;
        }

        public SimpleStats FetchSimpleOutStats()
        {
            return simpleOutStats;
        }

        void RequestTime(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var timeSyncRequest = new TimeSyncRequest(now);

            TimeSyncRequestSerializer.Serialize(outStream, timeSyncRequest);
        }

        void WritePing(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var pingRequest = new PingRequestHeader(now);

            PingRequestHeaderSerializer.Serialize(outStream, pingRequest);
            TendSerializer.Serialize(outStream, tendIn, tendOut);
        }

        void SendChallenge(IOutOctetStream outStream)
        {
            var challenge = new ChallengeRequest(challengeNonce);
            log.Debug($"Sending Challenge {challenge}");
            ChallengeRequestSerializer.Serialize(outStream, challenge);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        void SendConnectRequest(IOutOctetStream outStream)
        {
            var request = new ConnectRequest(remoteNonce, challengeNonce, sessionId, connectInfo);
            log.Debug($"Sending Connect request {request}");
            ConnectRequestSerializer.Serialize(outStream, request);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        void SendDisconnectRequest(IOutOctetStream outStream)
        {
            var request = new DisconnectRequest();
            log.Debug($"Sending disconnect request {request}");
            DisconnectRequestSerializer.Serialize(outStream, request);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        public void SendTimeSync(IOutOctetStream outStream)
        {
            log.Trace("Sending TimeSync!");

            RequestTime(outStream);
        }

        void SwitchState(ConnectionState newState, uint period)
        {
            log.Debug($"State: {newState} period:{period}");

            state = newState;
            stateChangeWait = period;
            lastStateChange = monotonicClock.NowMilliseconds();
#if false
            if (state == ConnectionState.Connected)
            {
                receiveStream.OnTimeSynced();
            }
#endif
        }

        public bool WriteUpdatePayload(IOutOctetStream octetStream)
        {
            var canSendUpdatePacket = tendOut.CanIncrementOutgoingSequence;
            pendingOutSequenceNumberUsed = false;
            if (canSendUpdatePacket)
            {
                outgoingSequenceNumber = outgoingSequenceNumber.Next();
                pendingOutSequenceNumber = outgoingSequenceNumber;
                pendingOutSequenceNumberUsed = true;
                var tendSequenceId = tendOut.IncreaseOutgoingSequenceId();
                WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
                TendSerializer.Serialize(octetStream, tendIn, tendOut);
                var now = monotonicClock.NowMilliseconds();
                timestampedHistory.SentPacket(outgoingSequenceNumber, now, log);
                log.Trace($"SendOneUpdatePacket: tendSeq{tendSequenceId}");

                return true;
            }

            WritePing(octetStream);
            return false;
        }

        public (IOutOctetStream, SequenceId, bool) PreparePacket()
        {
            var octetStream = new OutOctetStream();
            pendingOutOctetStream = octetStream;
            var wasUpdate = false;
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
                case ConnectionState.ConnectRequest:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
                    SendConnectRequest(octetStream);
                    break;
                case ConnectionState.Connected:
                    wasUpdate = WriteUpdatePayload(octetStream);
                    break;
                case ConnectionState.DisconnectRequest:
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, connectionId);
                    SendDisconnectRequest(octetStream);
                    break;
                case ConnectionState.Disconnected:
                    break;
                case ConnectionState.Idle:
                    break;
                default:
                    throw new ArgumentOutOfRangeException("unknown state");
            }

            var streamToReturn = wasUpdate ? octetStream : null;

            return (streamToReturn, pendingOutSequenceNumber, wasUpdate);
        }

        public void SendPreparedPacket()
        {
            var octetsToSend = pendingOutOctetStream.Close();
            var now = monotonicClock.NowMilliseconds();
            if (pendingOutSequenceNumberUsed)
            {
                outStatsCollector.SequencePacketSent(pendingOutSequenceNumber.Value, now, octetsToSend.Length);
            }
            else
            {
                outStatsCollector.PacketSent(now, octetsToSend.Length);
            }

            simpleOut.AddPacket(octetsToSend.Length);

            if (octetsToSend.Length > 0)
            {
                log.Trace($"Sending packet {ByteArrayToString(octetsToSend)}");

                udpClient.Send(octetsToSend);
                lastSentPackets = monotonicClock.NowMilliseconds();
            }
            else
            {
                log.Trace($"Strange nothing to send");
            }
        }

        void StartChallenge()
        {
            challengeNonce = RandomGenerator.RandomUInt();
            SwitchState(ConnectionState.Challenge, 0);
            --remainingChallengeAttempts;
        }

        void CheckDisconnect()
        {
            if (state == ConnectionState.Idle || state == ConnectionState.Challenge)
            {
                return;
            }

            var timeSinceReceivedHeader = monotonicClock.NowMilliseconds() - lastValidHeader;

            if (timeSinceReceivedHeader > disconnectTimeout * 1000)
            {
                log.Warning($"Disconnect detected! #{timeSinceReceivedHeader}");

                SwitchState(ConnectionState.Disconnected, 9999);
#if false
                receiveStream.Lost();
#endif
            }
        }

        private bool TimeHasPassedSinceLastPacket
        {
            get
            {
                var now = monotonicClock.NowMilliseconds();
                var diff = now - lastSentPackets;
                return diff >= 33;
            }
        }

        public bool CanSendUpdatePacket => state == ConnectionState.Connected && TimeHasPassedSinceLastPacket && tendOut.CanIncrementOutgoingSequence;

        public PacketBuffer IncomingPackets => incomingPacketBuffer;

        public PacketReceivedNotifications ReceivedNotifications => receivedNotifications;

        public void Update()
        {
            udpClient.Update();

            var diff = monotonicClock.NowMilliseconds() - lastStateChange;
            if (diff < stateChangeWait)
            {
                return;
            }

            var statsDiff = monotonicClock.NowMilliseconds() - lastStatsUpdate;
            if (statsDiff > 3000)
            {
                simpleInStats = simpleIn.GetStatsAndClear(statsDiff);
                simpleOutStats = simpleOut.GetStatsAndClear(statsDiff);
                lastStatsUpdate = monotonicClock.NowMilliseconds();
            }

            CheckDisconnect();

            switch (state)
            {
                case ConnectionState.Connected:
                    stateChangeWait = connectedPeriodInMs;
                    lastStateChange = monotonicClock.NowMilliseconds();
                    break;
            }
        }

        static string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);

            foreach (var b in ba)
            {
                hex.AppendFormat("{0:x2}", b);
            }
            return hex.ToString();
        }

        void OnChallengeResponse(IInOctetStream stream)
        {
            var response = ChallengeResponseHeaderDeserializer.Deserialize(stream);
            if (state != ConnectionState.Challenge)
            {
                return;
            }

            log.Debug($"Challenge response {response}");

            if (response.Nonce == challengeNonce)
            {
                remoteNonce = response.RemoteNonce;
                SwitchState(ConnectionState.ConnectRequest, 100);
            }
            else
            {
                if (remainingChallengeAttempts > 0)
                {
                    StartChallenge();
                }
                else
                {
                    udpClient.Stop();
                    SwitchState(ConnectionState.Disconnected, 0);
                }
            }
        }

        void OnConnectResponse(IInOctetStream stream)
        {
            var response = ConnectResponseDeserializer.Deserialize(stream);

            log.Debug($"We have a connection! {response}");

            connectionId = response.ConnectionId;
            if (syncTimeOnConnect)
            {
                SwitchState(ConnectionState.TimeSync, connectedPeriodInMs);
            }
            else
            {
                ConnectedAt = monotonicClock.NowMilliseconds();
                SwitchState(ConnectionState.Connected, 100);
            }
        }

        void OnPongResponseCmd(PongResponseHeader response) { }

        void OnPongResponse(IInOctetStream stream, long packetTime)
        {
            var pongResponse = PongResponseHeaderDeserializer.Deserialize(stream);

            OnPongResponseCmd(pongResponse);
            HandleTend(stream, packetTime);
        }

        void OnTimeSyncResponse(IInOctetStream stream)
        {
            log.Trace("On Time Sync response");

            if (state != ConnectionState.TimeSync)
            {
                log.Warning("We are not in timesync state anymore");
                return;
            }
            var echoedTicks = stream.ReadUint64();
            var latency = monotonicClock.NowMilliseconds() - (long)echoedTicks;
            var remoteTicks = stream.ReadUint64();
            latencies.AddLatency((ushort)latency);
            ushort averageLatency;
            var isStable = latencies.StableLatency(out averageLatency);

            if (isStable)
            {
                var remoteTimeIsNow = remoteTicks + averageLatency / (ulong)2;
                localMillisecondsToRemoteMilliseconds = (long)remoteTimeIsNow - monotonicClock.NowMilliseconds();
                latencies = new LatencyCollection();
                ConnectedAt = monotonicClock.NowMilliseconds();
                SwitchState(ConnectionState.Connected, 100);
            }
#if false
            else
            {
                log.Trace("Not stable yet, keep sending");
            }
#endif
        }

        void ReadOOB(IInOctetStream stream, long packetTime)
        {
            var cmd = stream.ReadUint8();

            switch (cmd)
            {
                case CommandValues.ChallengeResponse:
                    OnChallengeResponse(stream);
                    break;
                case CommandValues.ConnectResponse:
                    OnConnectResponse(stream);
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
                log.Trace($"we have deliveryInfo {deliveryInfo}");
                receivedNotifications.Enqueue(deliveryInfo.PacketSequenceId, deliveryInfo.WasDelivered);
                outStatsCollector.UpdateConfirmation(deliveryInfo.PacketSequenceId.Value, deliveryInfo.WasDelivered);
            }
        }

        SequenceId HandleTend(IInOctetStream stream, long packetTime)
        {
            var info = TendDeserializer.Deserialize(stream);
            log.Trace($"received tend {info} ");

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

#if false
                    simpleOut.AddLatency(latency);
#endif
                }
            }
            CallbackTend();

            return info.PacketId;
        }

        void ReadConnectionPacket(IInOctetStream stream, long nowMs)
        {
            var packetId = HandleTend(stream, nowMs);
            var count = stream.RemainingOctetCount;
            var payload = stream.ReadOctets(count);
            incomingPacketBuffer.AddPacket(packetId, payload);
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
                        simpleIn.AddPacket(packetOctetCount);
                        lastIncomingSequence = headerSequenceId;

                        if (mode == OobMode)
                        {
                            ReadOOB(stream, nowMs);
                        }
                        else if (mode == NormalMode)
                        {
                            ReadConnectionPacket(stream, nowMs);
                        }
                        else
                        {
                            log.Warning("Unknown mode {mode}");
                        }
                    }
                }
            }

            lastValidHeader = monotonicClock.NowMilliseconds();
        }

        void IPacketReceiver.ReceivePacket(byte[] octets, IPEndPoint fromEndpoint)
        {
            if (octets.Length < MinExpectedPacketLength)
            {
                return;
            }
            var nowMs = monotonicClock.NowMilliseconds();
            var stream = new InOctetStream(octets);
            var mode = stream.ReadUint8();
            log.Trace($"received packet:{octets.Length} mode:{mode}");

            ReadHeader(stream, mode, octets.Length, nowMs);
        }

        void IPacketReceiver.HandleException(Exception e)
        {
            log.Warning(e.ToString());

            if (e is SocketException se && se.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Suppress infinite warning for "System.Net.Sockets.SocketException (0x80004005): An existing connection was forcibly closed by the remote host."
                SwitchState(ConnectionState.Disconnected, 9999);
            }

#if false
            receiveStream.HandleException(e);
#endif
        }
        public ConnectionState ConnectionState => state;
    }
}
