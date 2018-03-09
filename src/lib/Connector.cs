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
﻿
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Flux.Client.Datagram;
using Piot.Brisk.Commands;
using Piot.Brisk.Serializers;
using Piot.Brisk.Time;
using Piot.Brook;
using Piot.Brook.Octet;
using Piot.Brook.Shared;
using Piot.Tend.Client;

namespace Piot.Brisk.Connect
{
	public enum ConnectionState
	{
		Challenge,
		TimeSync,
		Connected
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
		Queue<byte[]> messageQueue = new Queue<byte[]>();
		IReceiveStream receiveStream;
		DateTime lastStateChange = DateTime.UtcNow;
		uint stateChangeWait = 500;
		MonotonicClockStopwatch monotonicStopwatch = new MonotonicClockStopwatch ();
		IMonotonicClock monotonicClock;
		LatencyCollection latencies = new LatencyCollection ();
		long localMillisecondsToRemoteMilliseconds;

		public Connector(IReceiveStream receiveStream)
		{
			this.receiveStream = receiveStream;
			monotonicClock = monotonicStopwatch;
		}

		public void Connect(string host, int port)
		{
			udpClient = new Client(host, port, this);
			challengeNonce = RandomGenerator.RandomUInt();
		}

		public long RemoteMonotonicMilliseconds
		{
			get
			{
				if (state != ConnectionState.Connected)
				{
					throw new Exception ("You can only check remote time if connected!");
				}
				return monotonicClock.NowMilliseconds () + localMillisecondsToRemoteMilliseconds;
			}
		}

		public long RemoteMonotonicSimulationFrame
		{
			get
			{
				return ElapsedSimulationFrame.FromElapsedMilliseconds (RemoteMonotonicMilliseconds);
			}
		}

		static void WriteHeader(IOutOctetStream outStream, byte mode, byte sequence, ushort connectionIdToSend)
		{
			outStream.WriteUint8(mode);
			outStream.WriteUint8(sequence);
			outStream.WriteUint16(connectionIdToSend);
		}

		void RequestTime(IOutOctetStream outStream)
		{
			var now = monotonicClock.NowMilliseconds ();
			var timeSyncRequest = new TimeSyncRequest (now);

			TimeSyncRequestSerializer.Serialize (outStream, timeSyncRequest);
		}

		void SendChallenge(IOutOctetStream outStream)
		{
			var challenge = new ChallengeRequest(challengeNonce);

			ChallengeRequestSerializer.Serialize(outStream, challenge);
			lastStateChange = DateTime.UtcNow;
			stateChangeWait = 500;
		}

		public void SendTimeSync (IOutOctetStream outStream)
		{
			RequestTime (outStream);
		}

		void SwitchState(ConnectionState newState, uint period)
		{
			Console.Error.WriteLine ($"State:{newState} period:{period}");
			state = newState;
			stateChangeWait = period;
			lastStateChange = DateTime.UtcNow;
		}

		void SendOneUpdatePacket(IOutOctetStream octetStream)
		{
			if (messageQueue.Count > 0)
			{
				outgoingSequenceNumber = outgoingSequenceNumber.Next();
				WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
				var packetOctets = messageQueue.Dequeue();
				Console.Error.WriteLine($"Sending app octets {ByteArrayToString(packetOctets)}");
				octetStream.WriteOctets(packetOctets);
			}
		}

		void SendOnePacket()
		{
			var octetStream = new OutOctetStream();

			switch (state)
			{
			case ConnectionState.Challenge:
				WriteHeader(octetStream, OobMode, outSequenceNumber++, 0);
				SendChallenge(octetStream);
				break;
			case ConnectionState.TimeSync:
				WriteHeader (octetStream, OobMode, outSequenceNumber++, connectionId);
				SendTimeSync (octetStream);
				break;
			case ConnectionState.Connected:
				SendOneUpdatePacket(octetStream);
				break;
			}

			var octetsToSend = octetStream.Close();

			if (octetsToSend.Length > 0)
			{
				// Console.Error.WriteLine($"Sending packet {ByteArrayToString(octetsToSend)}");
				udpClient.Send(octetsToSend);
			}
		}

		public void Update()
		{
			var diff = DateTime.UtcNow - lastStateChange;

			if (diff.TotalMilliseconds < stateChangeWait)
			{
				return;
			}
			while (true)
			{
				SendOnePacket();

				if (state != ConnectionState.Connected)
				{
					break;
				}

				if (messageQueue.Count == 0)
				{
					break;
				}
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

			Console.Error.WriteLine($"Challenge response {nonce:X} {assignedConnectionId:X}");

			if (nonce == challengeNonce)
			{
				Console.Error.WriteLine($"We have a connection! {assignedConnectionId}");
				connectionId = assignedConnectionId;
				SwitchState (ConnectionState.TimeSync, 100);
			}
		}

		void OnTimeSyncResponse (IInOctetStream stream)
		{
			Console.Error.WriteLine("On Time Sync response");

			if (state != ConnectionState.TimeSync)
			{
				Console.Error.WriteLine("We are not in timesync state anymore");
				return;
			}
			var echoedTicks = stream.ReadUint64 ();
			var latency = monotonicClock.NowMilliseconds () - (long)echoedTicks;
			Console.Error.WriteLine($"Latency: {latency}");
			var remoteTicks = stream.ReadUint64 ();
			latencies.AddLatency ((ushort)latency);
			ushort averageLatency;
			var isStable = latencies.StableLatency (out averageLatency);

			if (isStable)
			{
				var remoteTimeIsNow = remoteTicks + averageLatency / (ulong)2;
				Console.Error.WriteLine($"We are stable! latency:{averageLatency}");
				localMillisecondsToRemoteMilliseconds = (long)remoteTimeIsNow - monotonicClock.NowMilliseconds ();
				latencies = new LatencyCollection ();
				SwitchState(ConnectionState.Connected, 100);
			}
			else
			{
				Console.Error.WriteLine("Not stable yet, keep sending");
			}
		}

		void ReadOOB(IInOctetStream stream)
		{
			Console.Error.WriteLine("OOB Packet");
			var cmd = stream.ReadUint8();
			switch (cmd)
			{
			case CommandValues.ChallengeResponse:
				OnChallengeResponse(stream);
				break;
			case CommandValues.TimeSyncResponse:
				OnTimeSyncResponse (stream);
				break;
			default:
				throw new Exception($"Unknown command {cmd}");
			}
		}

		void ReadConnectionPacket(IInOctetStream stream)
		{
			Console.Error.WriteLine("Connection packet!");
			receiveStream.Receive(stream);
		}

		public void Send(byte[] octets)
		{
			if (octets.Length > 450)
			{
				throw new Exception($"Too large packet! {octets.Length}");
			}
			messageQueue.Enqueue(octets);
		}

		void ReadHeader(IInOctetStream stream, byte mode)
		{
			var sequence = stream.ReadUint8();
			var assignedConnectionId = stream.ReadUint16();

			if (assignedConnectionId == 0)
			{
				ReadOOB(stream);
			}
			else
			{
				if (assignedConnectionId == connectionId)
				{
					var headerSequenceId = new SequenceId(sequence);

					if (lastIncomingSequence.IsValidSuccessor(headerSequenceId))
					{
						lastIncomingSequence = headerSequenceId;

						if (mode == OobMode)
						{
							ReadOOB (stream);
						}
						else
						{
							ReadConnectionPacket (stream);
						}
					}
					else
					{
						Console.Error.WriteLine($"Warning: out of sequence! expected {lastIncomingSequence.Next()} but received {headerSequenceId}");
					}
				}
			}
		}

		void IPacketReceiver.ReceivePacket (byte [] octets, IPEndPoint fromEndpoint)
		{
			if (octets.Length < 4)
			{
				return;
			}
			Console.Error.WriteLine ($"Received packet {ByteArrayToString (octets)}");
			var stream = new InOctetStream (octets);
			var mode = stream.ReadUint8 ();
			switch (mode)
			{
			case NormalMode:
				ReadHeader (stream, mode);
				break;
			case OobMode:
				ReadHeader (stream, mode);
				break;

			default:
				throw new Exception ($"Unknown mode {mode}");
			}
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
