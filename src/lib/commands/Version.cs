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
namespace Piot.Brisk.Commands
{
    public struct NameVersion
    {
        public Version Version;
        public string Name;

        public NameVersion(string name, Version version)
        {
            Name = name;
            Version = version;
        }

        public override string ToString()
        {
            return $"{Name} {Version}";
        }
    }

    public struct Version
    {
        public ushort Major;
        public ushort Minor;
        public ushort Patch;

        public string Prerelease;

        public Version(ushort major, ushort minor, ushort patch, string prerelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease;
        }


        public override string ToString()
        {
            var s = $"{Major}.{Minor}.{Patch}";

            if (Prerelease.Length != 0)
            {
                s += $"-{Prerelease}";
            }

            return s;
        }
    }
}
