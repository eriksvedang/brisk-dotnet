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
using System.Net;
using System.Text;
using Flux.Client.Datagram;
using Piot.Brisk.Commands;
using Piot.Brisk.Serializers;
using Piot.Brook;
using Piot.Brook.Octet;
using Piot.Brook.Shared;

namespace Piot.Brisk
{
	public class Connector : IPacketReceiver
	{
		enum State
		{
			Connecting,
			WaitingForChallengeResponse
		}

		const byte NormalMode = 0x01;

		State state = State.Connecting;
		Client udpClient;
		byte outSequenceNumber = 0;
		DateTime lastStateChange = DateTime.UtcNow;
		public Connector()
		{
		}

		public void Connect(string host, int port)
		{
			udpClient = new Client(host, port, this);
		}

		void WriteHeader(IOutOctetStream outStream, byte mode, byte sequence, ushort connectionId)
		{
			outStream.WriteUint8(mode);
			outStream.WriteUint8(sequence);
			outStream.WriteUint16(connectionId);
		}

		public void UpdateConnecting(IOutOctetStream outStream)
		{
			var challenge = new ChallengeRequest();

			ChallengeRequestSerializer.Serialize(outStream, challenge);
			state = State.WaitingForChallengeResponse;
			lastStateChange = DateTime.UtcNow;
		}

		public void Update()
		{
			// Receive Packet

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

		public void ReceivePacket(byte[] octets, IPEndPoint fromEndpoint)
		{
			Console.WriteLine($"Received packet {ByteArrayToString(octets)}");
		}
	}
}
