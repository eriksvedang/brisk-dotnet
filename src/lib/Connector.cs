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
        public ConnectionState ConnectionState { get; private set; }
        public UniqueSessionID SessionId { get; }
        public PacketBuffer IncomingPackets { get; }
        public PacketReceivedNotifications ReceivedNotifications { get; } = new PacketReceivedNotifications();
        public AbsoluteSimulationFrame RemoteMonotonicSimulationFrame => ElapsedSimulationFrame.FromElapsedMilliseconds(RemoteMonotonicMilliseconds);
        public long ConnectedAt { get; private set; }

        public long RemoteMonotonicMilliseconds
        {
            get
            {
                if (ConnectionState != ConnectionState.Connected)
                {
                    throw new Exception("You can only check remote time if connected!");
                }

                return monotonicClock.NowMilliseconds() + localMillisecondsToRemoteMilliseconds;
            }
        }

        public bool TimeToSendPacket
        {
            get
            {
                var now = monotonicClock.NowMilliseconds();
                var diff = now - lastSentPackets;
                return diff >= packetSendPeriodInMs;
            }
        }

        private const byte NormalMode = 0x01;
        private const byte OobMode = 0x02;
        private const int MaxChallengeAttempts = 3;
        private const int MinExpectedPacketLength = 4;
        private const uint MinFrequency = 1;
        private const uint MaxFrequency = 60;
        private const float DisconnectTimeout = 3;

        private Client udpClient;
        private byte outSequenceNumber;
        private uint challengeNonce;
        private ushort connectionId;
        private SequenceId lastIncomingSequence = SequenceId.Max;
        private SequenceId outgoingSequenceNumber = SequenceId.Max;

        private long lastStateChange;
        private uint stateChangeWait = 500;
        private MonotonicClockStopwatch monotonicStopwatch = new MonotonicClockStopwatch();
        private IMonotonicClock monotonicClock;
        private LatencyCollection latencies = new LatencyCollection();
        private long localMillisecondsToRemoteMilliseconds;
        private ILog log;
        private long lastValidHeader;
        private OutgoingLogic tendOut = new OutgoingLogic();
        private IncomingLogic tendIn = new IncomingLogic();
        private TimeStampedPacketHistory timestampedHistory = new TimeStampedPacketHistory();
        private IInStatsCollector inStatsCollector = new InStatsCollector();
        private IOutStatsCollector outStatsCollector = new OutStatsCollector();
        private uint connectedPeriodInMs;
        private uint packetSendPeriodInMs;
        private uint packetSendMaxOctetsPerSecond;
        private long startSendingPacketsTime = -1;
        private long lastSentPackets;
        private long maxSendPacketsMillisecondsHint;
        private SequenceId pendingOutSequenceNumber;
        private string lastHost;

        private SimpleStatsCollector simpleIn = new SimpleStatsCollector();
        private SimpleStatsCollector simpleOut = new SimpleStatsCollector();
        private SimpleStats simpleInStats;
        private SimpleStats simpleOutStats;
        private long lastStatsUpdate;

        private int remainingChallengeAttempts = 0;
        private bool syncTimeOnConnect;
        private uint remoteNonce;
        private ConnectInfo connectInfo;

        public Connector(ILog log, uint connectStateChangeFrequency, uint packetSendFrequency, uint packetSendMaxOctetsPerSecond, uint maxSendPacketsMillisecondsHint, IPort port, bool syncTimeOnConnect)
        {
            this.log = log;

            this.packetSendMaxOctetsPerSecond = packetSendMaxOctetsPerSecond;
            this.maxSendPacketsMillisecondsHint = maxSendPacketsMillisecondsHint;

            connectStateChangeFrequency = Math.Max(Math.Min(connectStateChangeFrequency, MaxFrequency), MinFrequency);
            packetSendFrequency = Math.Max(Math.Min(packetSendFrequency, MaxFrequency), MinFrequency);

            connectedPeriodInMs = 1000 / connectStateChangeFrequency;
            packetSendPeriodInMs = 1000 / packetSendFrequency;

            monotonicClock = monotonicStopwatch;
            IncomingPackets = new PacketBuffer(monotonicClock);
            SessionId = RandomGenerator.RandomUniqueSessionId();
            udpClient = new Client(this, port);

            this.syncTimeOnConnect = syncTimeOnConnect;

            Reset();
        }

        private void Reset()
        {
            ConnectionState = ConnectionState.Idle;
            lastIncomingSequence = SequenceId.Max;
            outgoingSequenceNumber = SequenceId.Max;
            pendingOutSequenceNumber = outgoingSequenceNumber;
            outSequenceNumber = 0;
            challengeNonce = 0;
            connectInfo = new ConnectInfo();
            tendIn.Clear();
            tendOut.Clear();
            IncomingPackets.Clear();
            ReceivedNotifications.Clear();
            lastStateChange = monotonicClock.NowMilliseconds();
            lastValidHeader = monotonicClock.NowMilliseconds();
            startSendingPacketsTime = -1;
            lastSentPackets = monotonicClock.NowMilliseconds();
            remainingChallengeAttempts = MaxChallengeAttempts;
        }

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
            UpdateState();
            udpClient.Stop();
            ReceivedNotifications.Clear();
            IncomingPackets.Clear();
            SwitchState(ConnectionState.Disconnected, 0);
        }

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

        private void RequestTime(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var timeSyncRequest = new TimeSyncRequest(now);

            TimeSyncRequestSerializer.Serialize(outStream, timeSyncRequest);
        }

        private void WritePing(IOutOctetStream outStream)
        {
            var now = monotonicClock.NowMilliseconds();
            var pingRequest = new PingRequestHeader(now);

            PingRequestHeaderSerializer.Serialize(outStream, pingRequest);
            TendSerializer.Serialize(outStream, tendIn, tendOut);
        }

        private void WriteChallenge(IOutOctetStream outStream)
        {
            var challenge = new ChallengeRequest(challengeNonce);
            log.Debug($"Sending Challenge {challenge}");
            ChallengeRequestSerializer.Serialize(outStream, challenge);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        private void WriteConnectRequest(IOutOctetStream outStream)
        {
            var request = new ConnectRequest(remoteNonce, challengeNonce, SessionId, connectInfo);
            log.Debug($"Sending Connect request {request}");
            ConnectRequestSerializer.Serialize(outStream, request);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        private void WriteDisconnectRequest(IOutOctetStream outStream)
        {
            var request = new DisconnectRequest();
            log.Debug($"Sending disconnect request {request}");
            DisconnectRequestSerializer.Serialize(outStream, request);
            lastStateChange = monotonicClock.NowMilliseconds();
            stateChangeWait = 500;
        }

        private void WriteTimeSync(IOutOctetStream outStream)
        {
            log.Trace("Sending TimeSync!");

            RequestTime(outStream);
        }

        private void SwitchState(ConnectionState newState, uint period)
        {
            log.Debug($"State: {newState} period:{period}");

            ConnectionState = newState;
            stateChangeWait = period;
            lastStateChange = monotonicClock.NowMilliseconds();
#if false
            if (state == ConnectionState.Connected)
            {
                receiveStream.OnTimeSynced();
            }
#endif
        }

        public void StartSendingPackets()
        {
            startSendingPacketsTime = monotonicClock.NowMilliseconds();
        }

        public void SendPacket(IOutOctetStream octetStream, bool isSequenced)
        {
            var octetsToSend = octetStream.Close();
            var now = monotonicClock.NowMilliseconds();

            if (isSequenced)
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
            }
            else
            {
                log.Trace($"Strange nothing to send");
            }
        }

        public void DoneSendingPackets(bool anythingSent)
        {
            if (anythingSent)
            {
                lastSentPackets = monotonicClock.NowMilliseconds();
            }
            startSendingPacketsTime = -1;
        }

        public void UpdateState()
        {
            OutOctetStream octetStream;

            switch (ConnectionState)
            {
                case ConnectionState.Challenge:
                    octetStream = new OutOctetStream();
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
                    WriteChallenge(octetStream);
                    StartSendingPackets();
                    SendPacket(octetStream, false);
                    DoneSendingPackets(true);
                    break;
                case ConnectionState.TimeSync:
                    octetStream = new OutOctetStream();
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, connectionId);
                    WriteTimeSync(octetStream);
                    StartSendingPackets();
                    SendPacket(octetStream, false);
                    DoneSendingPackets(true);
                    break;
                case ConnectionState.ConnectRequest:
                    octetStream = new OutOctetStream();
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
                    WriteConnectRequest(octetStream);
                    StartSendingPackets();
                    SendPacket(octetStream, false);
                    DoneSendingPackets(true);
                    break;
                case ConnectionState.DisconnectRequest:
                    octetStream = new OutOctetStream();
                    WriteHeader(octetStream, OobMode, outSequenceNumber++, connectionId);
                    WriteDisconnectRequest(octetStream);
                    StartSendingPackets();
                    SendPacket(octetStream, false);
                    DoneSendingPackets(true);
                    break;
                case ConnectionState.Disconnected:
                case ConnectionState.Connected:
                case ConnectionState.Idle:
                    break;
                default:
                    throw new ArgumentOutOfRangeException("unknown state");
            }
        }

        public SequenceId WriteUpdatePayload(IOutOctetStream octetStream)
        {
            outgoingSequenceNumber = outgoingSequenceNumber.Next();
            pendingOutSequenceNumber = outgoingSequenceNumber;
            var tendSequenceId = tendOut.IncreaseOutgoingSequenceId();
            WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
            TendSerializer.Serialize(octetStream, tendIn, tendOut);
            var now = monotonicClock.NowMilliseconds();
            timestampedHistory.SentPacket(outgoingSequenceNumber, now, log);
            log.Trace($"SendOneUpdatePacket: tendSeq{tendSequenceId}");

            return pendingOutSequenceNumber;
        }

        public void WritePingPayload(IOutOctetStream octetStream)
        {
            WriteHeader(octetStream, OobMode, outgoingSequenceNumber.Value, connectionId);
            WritePing(octetStream);
        }

        private void StartChallenge()
        {
            challengeNonce = RandomGenerator.RandomUInt();
            SwitchState(ConnectionState.Challenge, 0);
            --remainingChallengeAttempts;
        }

        private void CheckDisconnect()
        {
            if (ConnectionState == ConnectionState.Idle ||
                ConnectionState == ConnectionState.Challenge ||
                ConnectionState == ConnectionState.DisconnectRequest ||
                ConnectionState == ConnectionState.Disconnected)
            {
                return;
            }

            var timeSinceReceivedHeader = monotonicClock.NowMilliseconds() - lastValidHeader;

            if (timeSinceReceivedHeader > DisconnectTimeout * 1000)
            {
                log.Warning($"Disconnect detected! #{timeSinceReceivedHeader}");

                SwitchState(ConnectionState.Disconnected, 9999);
#if false
                receiveStream.Lost();
#endif
            }
        }

        public bool CanSendUpdatePacket()
        {
            //TODO: calculate bandwidth and test it here.

            var haveTimeToSendPacket = true;

            if (startSendingPacketsTime >= 0)
            {
                var timeDiff = monotonicClock.NowMilliseconds() - startSendingPacketsTime;
                haveTimeToSendPacket = (timeDiff <= maxSendPacketsMillisecondsHint);
            }

            return ConnectionState == ConnectionState.Connected &&
                haveTimeToSendPacket &&
                TimeToSendPacket &&
                tendOut.CanIncrementOutgoingSequence;
        }

        public bool CanSendPingPacket()
        {
            return ConnectionState == ConnectionState.Connected && TimeToSendPacket;
        }

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

            switch (ConnectionState)
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

        private void OnChallengeResponse(IInOctetStream stream)
        {
            var response = ChallengeResponseHeaderDeserializer.Deserialize(stream);
            if (ConnectionState != ConnectionState.Challenge)
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

        private void OnConnectResponse(IInOctetStream stream)
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

        private void OnPongResponseCmd(PongResponseHeader response) { }

        private void OnPongResponse(IInOctetStream stream, long packetTime)
        {
            var pongResponse = PongResponseHeaderDeserializer.Deserialize(stream);

            OnPongResponseCmd(pongResponse);
            HandleTend(stream, packetTime);
        }

        private void OnTimeSyncResponse(IInOctetStream stream)
        {
            log.Trace("On Time Sync response");

            if (ConnectionState != ConnectionState.TimeSync)
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

        private void ReadOOB(IInOctetStream stream, long packetTime)
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

        private void CallbackTend()
        {
            while (tendOut.Count > 0)
            {
                var deliveryInfo = tendOut.Dequeue();
                log.Trace($"we have deliveryInfo {deliveryInfo}");
                ReceivedNotifications.Enqueue(deliveryInfo.PacketSequenceId, deliveryInfo.WasDelivered);
                outStatsCollector.UpdateConfirmation(deliveryInfo.PacketSequenceId.Value, deliveryInfo.WasDelivered);
            }
        }

        private SequenceId HandleTend(IInOctetStream stream, long packetTime)
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

        private void ReadConnectionPacket(IInOctetStream stream, long nowMs)
        {
            var packetId = HandleTend(stream, nowMs);
            var count = stream.RemainingOctetCount;
            var payload = stream.ReadOctets(count);
            IncomingPackets.AddPacket(packetId, payload);
        }

        private void ReadHeader(IInOctetStream stream, byte mode, int packetOctetCount, long nowMs)
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
    }
}
