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
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using Flux.Client.Datagram;
using Piot.Brisk.Commands;
using Piot.Brisk.Serializers;
using Piot.Brook;
using Piot.Brook.Octet;
using Piot.Brook.Shared;
using Piot.Tend.Client;

namespace Piot.Brisk.Connect
{
	public class Connector : IPacketReceiver
	{
		enum State
		{
			Connecting,
			WaitingForChallengeResponse,
			Connected
		}

		const byte NormalMode = 0x01;

		State state = State.Connecting;
		Client udpClient;
		byte outSequenceNumber = 0;
		uint challengeNonce;
		ushort connectionId;
		SequenceId lastIncomingSequence = SequenceId.Max;
		SequenceId outgoingSequenceNumber = SequenceId.Max;
		DateTime lastStateChange = DateTime.UtcNow;
		Queue<byte[]> messageQueue = new Queue<byte[]>();
		IReceiveStream receiveStream;

		public Connector(IReceiveStream receiveStream)
		{
			this.receiveStream = receiveStream;
		}

		public void Connect(string host, int port)
		{
			udpClient = new Client(host, port, this);
			challengeNonce = RandomGenerator.RandomUInt();
		}

		void WriteHeader(IOutOctetStream outStream, byte mode, byte sequence, ushort connectionId)
		{
			outStream.WriteUint8(mode);
			outStream.WriteUint8(sequence);
			outStream.WriteUint16(connectionId);
		}

		public void UpdateConnecting(IOutOctetStream outStream)
		{
			var challenge = new ChallengeRequest(challengeNonce);

			ChallengeRequestSerializer.Serialize(outStream, challenge);
			state = State.WaitingForChallengeResponse;
			lastStateChange = DateTime.UtcNow;
		}

		public void Update()
		{
			var diff = DateTime.UtcNow - lastStateChange;

			if (diff.TotalMilliseconds < 500)
			{
				return;
			}
			lastStateChange = DateTime.UtcNow;

			Console.WriteLine("Pulse!");
			var octetQueue = new OctetQueue(500);
			var octetStream = new OutOctetStream();

			switch (state)
			{
			case State.Connecting:
				WriteHeader(octetStream, NormalMode, outSequenceNumber++, 0);
				UpdateConnecting(octetStream);
				break;
			case State.Connected:

				if (messageQueue.Count > 0)
				{
					outgoingSequenceNumber = outgoingSequenceNumber.Next();
					WriteHeader(octetStream, NormalMode, outgoingSequenceNumber.Value, connectionId);
					var packetOctets = messageQueue.Dequeue();
					octetStream.WriteOctets(packetOctets);
				}

				break;
			}

			var octetsToSend = octetStream.Close();

			if (octetsToSend.Length > 0)
			{
				Console.WriteLine($"Sending packet {ByteArrayToString(octetsToSend)}");
				udpClient.Send(octetsToSend);
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
			var nonce = stream.ReadUint32();
			var assignedConnectionId = stream.ReadUint16();

			Console.WriteLine($"Challenge response {nonce:X} {assignedConnectionId:X}");

			if (nonce == challengeNonce)
			{
				Console.WriteLine($"We have a connection! {assignedConnectionId}");
				this.connectionId = assignedConnectionId;
				state = State.Connected;
				lastStateChange = DateTime.UtcNow;
			}
		}

		void ReadOOB(IInOctetStream stream)
		{
			Console.WriteLine("OOB Packet");
			var cmd = stream.ReadUint8();
			switch (cmd)
			{
			case CommandValues.ChallengeResponse:
				OnChallengeResponse(stream);
				break;
			default:
				throw new Exception($"Unknown command {cmd}");
			}
		}

		void ReadConnectionPacket(IInOctetStream stream)
		{
			Console.WriteLine("Connection packet!");
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

		private void ReadHeader(IInOctetStream stream)
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
						ReadConnectionPacket(stream);
					}
					else
					{
						Console.WriteLine($"Warning: out of sequence! expected {lastIncomingSequence.Next()} but received {headerSequenceId}");
					}
				}
			}
		}

		public void ReceivePacket(byte[] octets, IPEndPoint fromEndpoint)
		{
			Console.WriteLine($"Received packet {ByteArrayToString(octets)}");
			var stream = new InOctetStream(octets);
			var mode = stream.ReadUint8();
			switch (mode)
			{
			case 1: // Normal
				ReadHeader(stream);
				break;
			default:
				throw new Exception($"Unknown mode {mode}");
			}
		}
	}
}
