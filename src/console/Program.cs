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
        ﻿ using System;

namespace BriskConsole
{
	class Program
	{
		public static void ParseHostString(string hostString, out string hostName, out int port)
		{
			hostName = hostString;

			if (hostString.Contains(":"))
			{
				string[] hostParts = hostString.Split(':');

				if (hostParts.Length == 2)
				{
					int parsedPort;
					int.TryParse(hostParts[1], out parsedPort);

					hostName = hostParts[0];
					port = parsedPort;
				}
				else
				{
					throw new Exception("Illegal format string");
				}
			}
			else
			{
				hostName = hostString;
				port = 32001;
			}
		}

		static void Main(string[] args)
		{
			Console.Error.WriteLine("Brisk Console v0.1");

			if (args.Length < 1)
			{
				return;
			}
			var hostString = args[0];
			string hostname;
			int port;
			ParseHostString(hostString, out hostname, out port);
			Console.Error.WriteLine($"Trying to connect to '{hostname}' {port}");
			var client = new Client(hostname, port);

			while (true)
			{
				client.Update();
			}
		}
	}
}
