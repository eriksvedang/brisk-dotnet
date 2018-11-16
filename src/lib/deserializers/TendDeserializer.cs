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
using Piot.Brisk.Commands;
using Piot.Brook;
using Piot.Tend.Client;

namespace Piot.Brisk.Serializers
{
	public static class TendDeserializer
	{
		public struct Info
		{
			public SequenceId PacketId;
			public Header Header;
		};

		public static Info Deserialize(IInOctetStream stream)
		{
			var packetSequenceId = stream.ReadUint8();
			var receivedByRemoteSequenceId = stream.ReadUint8();
			var receiveMask = stream.ReadUint32();
			var header = new Header(new SequenceId(receivedByRemoteSequenceId), new ReceiveMask(receiveMask));

			var info = new Info
			{
				PacketId = new SequenceId(packetSequenceId), Header = header
			};

			return info;
		}
	}
}