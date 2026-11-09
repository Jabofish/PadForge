using System;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Tests
{
    /// <summary>Frames of the Flydigi Vader 5 Pro vendor interface. The expected bytes come from
    /// tux-invader's tests (live red) and VaderBatteryTray's captured Space Station frames.</summary>
    public class FlydigiVader5ProtocolTests
    {
        private static string Hex(byte[] b, int n) => Convert.ToHexString(b, 0, n).ToLowerInvariant();

        [Fact]
        public void LiveColor_red_matches_tux_invader_vector()
            => Assert.Equal("5aa5f505ff0000f900", Hex(FlydigiVader5Protocol.LiveColor(0xFF0000), 9));

        [Fact]
        public void InfoRequest_matches_init_vector()
            => Assert.Equal("5aa5010203", Hex(FlydigiVader5Protocol.InfoRequest(), 5));

        [Fact]
        public void Frames_are_32_bytes_and_report_is_33()
        {
            Assert.Equal(32, FlydigiVader5Protocol.LiveColor(0x123456).Length);
            var report = FlydigiVader5Protocol.AsReport(FlydigiVader5Protocol.LiveColor(0x123456));
            Assert.Equal(33, report.Length);
            Assert.Equal(0, report[0]);
            Assert.Equal(0x5A, report[1]);
        }

        [Fact]
        public void StaticOff_prelude_matches_captured_frame()
        {
            var frames = FlydigiVader5Protocol.StaticFrames(0, 0, 100, FlydigiVader5Protocol.ModeOff);
            Assert.Equal(4, frames.Length);
            // VaderBatteryTray prelude: 5A A5 A8 06 00 00 03 14 C5
            Assert.Equal("5aa5a80600000314c5", Hex(frames[0], 9));
        }

        [Fact]
        public void StaticSolid_config_and_zones_match_VaderBatteryTray_layout()
        {
            var f = FlydigiVader5Protocol.StaticFrames(0, 0x102030, 80, FlydigiVader5Protocol.ModeSolid);
            // config report: seq 0, byte[6]=03, byte[10]=01, byte[11]=brightness, byte[12]=10 zones, byte[13]=mode
            Assert.Equal(0xA9, f[1][2]);
            Assert.Equal(0x17, f[1][3]);
            Assert.Equal(0, f[1][4]);
            Assert.Equal(0x03, f[1][6]);
            Assert.Equal(0x01, f[1][10]);
            Assert.Equal(80, f[1][11]);
            Assert.Equal(10, f[1][12]);
            Assert.Equal(0x05, f[1][13]);
            for (int i = 15; i <= 24; i++) Assert.Equal(0xFF, f[1][i]);
            // zones: first 20 bytes in packet 1, last 10 in packet 2
            Assert.Equal(0x10, f[2][5]); Assert.Equal(0x20, f[2][6]); Assert.Equal(0x30, f[2][7]);
            Assert.Equal(0x30, f[3][5 + 9]);
            Assert.Equal(0x00, f[3][5 + 10]);
            // checksum = sum of bytes 2..24 (the 5A A5 magic excluded)
            foreach (var p in f)
            {
                int sum = 0;
                for (int i = 2; i < 2 + p[3]; i++) sum += p[i];
                Assert.Equal((byte)(sum & 0xFF), p[2 + p[3]]);
            }
        }

        [Fact]
        public void BlobFrames_rewrite_a_blob_in_20_byte_chunks()
        {
            var blob = new byte[320];
            for (int i = 0; i < blob.Length; i++) blob[i] = (byte)i;
            var frames = FlydigiVader5Protocol.BlobFrames(1, blob);
            Assert.Equal(17, frames.Length);
            Assert.Equal(0xA8, frames[0][2]);
            Assert.Equal(1, frames[0][4]);   // profile
            Assert.Equal(16, frames[0][6]);  // packet count
            Assert.Equal(0xA9, frames[16][2]);
            Assert.Equal(15, frames[16][4]);
            Assert.Equal((byte)44, frames[16][5]);
        }

        [Fact]
        public void ActiveProfile_decodes_switch_mode()
        {
            var r = new byte[32];
            r[5] = 6; Assert.Equal(2, FlydigiVader5Protocol.ActiveProfile(r));
            r[5] = 3; Assert.Equal(3, FlydigiVader5Protocol.ActiveProfile(r));
            r[5] = 9; Assert.Equal(0, FlydigiVader5Protocol.ActiveProfile(r));
        }

        [Fact]
        public void Extract_handles_report_id_prefix()
        {
            var withId = new byte[33];
            withId[1] = 0x5A; withId[2] = 0xA5; withId[3] = 0x01;
            Assert.Equal(0x01, FlydigiVader5Protocol.Extract(withId, 33)[2]);
            var without = new byte[32];
            without[0] = 0x5A; without[1] = 0xA5; without[2] = 0xA1;
            Assert.Equal(0xA1, FlydigiVader5Protocol.Extract(without, 32)[2]);
            Assert.Null(FlydigiVader5Protocol.Extract(new byte[33], 33));
        }

        [Fact]
        public void IsConfigCollection_gates_on_vendor_usage_page_and_report_sizes()
        {
            HidCollectionInfo C(ushort vid, ushort page, ushort usage = 1, ushort inLen = 33, ushort outLen = 33) => new()
            { Path = @"\\?\hid#x", VendorId = vid, ProductId = 0x2401, UsagePage = page, Usage = usage, InputReportLength = inLen, OutputReportLength = outLen };
            Assert.True(FlydigiVader5Protocol.IsConfigCollection(C(0x37D7, 0xFFA0)));
            Assert.False(FlydigiVader5Protocol.IsConfigCollection(C(0x045E, 0xFFA0)));
            Assert.False(FlydigiVader5Protocol.IsConfigCollection(C(0x37D7, 0x0001)));
            Assert.False(FlydigiVader5Protocol.IsConfigCollection(C(0x37D7, 0xFFA0, 2)));
            Assert.False(FlydigiVader5Protocol.IsConfigCollection(C(0x37D7, 0xFFA0, 1, 0, 33)));
            Assert.False(FlydigiVader5Protocol.IsConfigCollection(null));
        }

        [Fact]
        public void Family_is_appended_and_unit_family_is_not_shared()
        {
            Assert.Equal(10, (int)OutputFamily.FlydigiVader5Unit);
            Assert.False(new OutputPath(OutputFamily.FlydigiVader5Unit, "x").Shared);
        }
    }
}
