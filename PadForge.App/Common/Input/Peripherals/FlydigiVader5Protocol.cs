using System;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Flydigi Vader 5 Pro vendor-interface frames (usage page 0xFFA0), for
    /// <see cref="FlydigiVader5Backend"/>. Every frame is 32 bytes:
    /// <c>5A A5 cmd len payload checksum</c>, where len = 2 + payload length
    /// and checksum = sum of every byte after the magic, mod 256.
    ///
    /// <para>Sources (see docs/PROTOCOL.md): tux-invader's protocol.py (Linux,
    /// cable and 2.4G dongle; the 0xF5 live color and the A1/A7/A8/A9 blob
    /// exchange) and VaderBatteryTray's VaderLedProtocol.cs (Windows, built
    /// from USBPcap captures of Flydigi Space Station; the off/solid blob).
    /// Nothing here is saved to the controller: a power cycle brings back its
    /// stored lights.</para>
    ///
    /// <para>Deliberately never sent: 0x1F (firmware mode), 0xFD (reset),
    /// 0xFE, 0xA6 (save).</para>
    /// </summary>
    internal static class FlydigiVader5Protocol
    {
        internal const ushort VendorId = 0x37D7;
        internal const ushort ConfigUsagePage = 0xFFA0;
        internal const ushort ConfigUsage = 0x0001;
        /// <summary>Model byte (reply[5]) of a Vader 5 Pro in the info reply.</summary>
        internal const byte ModelVader5Pro = 0x82;

        internal const int PacketSize = 32;
        /// <summary>Windows output report: report ID 0, then the 32 bytes.</summary>
        internal const int ReportSize = PacketSize + 1;
        internal const int BlobChunk = 20;
        internal const int ZoneCount = 10;

        internal const byte CmdInfo = 0x01;
        internal const byte CmdProfileVersions = 0xA1;
        internal const byte CmdLedRead = 0xA7;
        internal const byte CmdLedWriteStart = 0xA8;
        internal const byte CmdLedWritePack = 0xA9;
        internal const byte CmdLedTestColor = 0xF5;

        internal const byte ModeSolid = 0x05;
        internal const byte ModeOff = 0x06;

        /// <summary>Whether a collection is a Flydigi config interface. Model
        /// is checked later, on the info reply, so a dongle's product ID
        /// need not be known.</summary>
        internal static bool IsConfigCollection(HidCollectionInfo c)
            => c != null && c.Path != null && c.VendorId == VendorId
               && c.UsagePage == ConfigUsagePage && c.Usage == ConfigUsage
               && c.InputReportLength > 0 && c.OutputReportLength > 0;

        /// <summary>A bare command: 5A A5 cmd args checksum, padded to 32.</summary>
        internal static byte[] Command(byte cmd, params byte[] args)
        {
            var p = new byte[PacketSize];
            p[0] = 0x5A; p[1] = 0xA5; p[2] = cmd;
            int n = 3;
            foreach (byte a in args) p[n++] = a;
            int sum = 0;
            for (int i = 2; i < n; i++) sum += p[i];
            p[n] = (byte)(sum & 0xFF);
            return p;
        }

        /// <summary>A command with its length byte filled in.</summary>
        internal static byte[] Request(byte cmd, params byte[] payload)
        {
            var args = new byte[payload.Length + 1];
            args[0] = (byte)(2 + payload.Length);
            Array.Copy(payload, 0, args, 1, payload.Length);
            return Command(cmd, args);
        }

        internal static byte[] InfoRequest() => Command(CmdInfo, 0x02);
        internal static byte[] ProfileVersionsRequest() => Request(CmdProfileVersions);
        internal static byte[] LedReadRequest(int profile) => Request(CmdLedRead, (byte)profile, BlobChunk);
        internal static byte[] LedWriteStart(int profile, int packets) => Request(CmdLedWriteStart, (byte)profile, 0, (byte)packets, BlobChunk);

        internal static byte[] LedWritePack(int index, byte[] chunk)
        {
            var payload = new byte[chunk.Length + 1];
            payload[0] = (byte)index;
            Array.Copy(chunk, 0, payload, 1, chunk.Length);
            return Request(CmdLedWritePack, payload);
        }

        /// <summary>One color on the whole strip, right away, not saved.</summary>
        internal static byte[] LiveColor(int rgb)
            => Request(CmdLedTestColor, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        /// <summary>The active on-board profile (0-3) from a profile-versions reply.</summary>
        internal static int ActiveProfile(byte[] reply)
        {
            byte raw = reply[5];
            return raw >= 4 && raw <= 7 ? raw - 4 : raw <= 3 ? raw : 0;
        }

        /// <summary>The frames that write a whole LED blob (what a read returned) back.</summary>
        internal static byte[][] BlobFrames(int profile, byte[] blob)
        {
            int packets = (blob.Length + BlobChunk - 1) / BlobChunk;
            var frames = new byte[packets + 1][];
            frames[0] = LedWriteStart(profile, packets);
            for (int i = 0; i < packets; i++)
            {
                var chunk = new byte[Math.Min(BlobChunk, blob.Length - i * BlobChunk)];
                Array.Copy(blob, i * BlobChunk, chunk, 0, chunk.Length);
                frames[i + 1] = LedWritePack(i, chunk);
            }
            return frames;
        }

        /// <summary>The 3-packet static blob captured from Flydigi Space Station
        /// (all ten zones one color, or off for <see cref="ModeOff"/>).</summary>
        internal static byte[][] StaticFrames(int profile, int rgb, byte brightnessPercent, byte mode)
        {
            var config = new byte[BlobChunk];
            config[1] = 0x03;
            config[5] = 0x01;
            config[6] = brightnessPercent;
            config[7] = ZoneCount;
            config[8] = mode;
            for (int i = 10; i < BlobChunk; i++) config[i] = 0xFF;

            var zones = new byte[ZoneCount * 3];
            for (int z = 0; z < ZoneCount; z++)
            {
                zones[z * 3] = (byte)(rgb >> 16);
                zones[z * 3 + 1] = (byte)(rgb >> 8);
                zones[z * 3 + 2] = (byte)rgb;
            }
            var first = new byte[BlobChunk];
            var second = new byte[BlobChunk];
            Array.Copy(zones, 0, first, 0, 20);
            Array.Copy(zones, 20, second, 0, 10);
            return new[]
            {
                LedWriteStart(profile, 3),
                LedWritePack(0, config),
                LedWritePack(1, first),
                LedWritePack(2, second),
            };
        }

        /// <summary>A 32-byte packet as a Windows output report (report ID 0 first).</summary>
        internal static byte[] AsReport(byte[] packet)
        {
            var r = new byte[ReportSize];
            Array.Copy(packet, 0, r, 1, Math.Min(packet.Length, PacketSize));
            return r;
        }

        /// <summary>Finds the 5A A5 packet in an input report as Windows delivers
        /// it (with or without the report ID byte in front). Null when it is not one.</summary>
        internal static byte[] Extract(byte[] buffer, int count)
        {
            if (buffer == null) return null;
            int off = count > 0 && buffer[0] == 0x5A ? 0 : 1;
            if (count < off + 6 || buffer[off] != 0x5A || buffer[off + 1] != 0xA5) return null;
            var p = new byte[count - off];
            Array.Copy(buffer, off, p, 0, p.Length);
            return p;
        }
    }
}
