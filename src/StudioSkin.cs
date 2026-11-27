using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace ScreenStudio
{
    internal sealed class StudioSkin
    {
        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string Motif { get; private set; }
        internal string StickerFile { get; private set; }
        internal Color Paper, Ink, Muted, Line, Rail;
        internal Color Accent, Tint, PrimaryFill, Secondary, SecondaryTint, SecondaryFill;
        internal Color Note, NoteInk, NoteLine, Success, SuccessFill, OffTrack;
        internal Color CoverStart, CoverEnd, Pattern;

        private static Color Rgb(int value) { return Color.FromArgb(unchecked((int)0xff000000) | value); }
        private StudioSkin() { }

        internal static readonly StudioSkin Strawberry = new StudioSkin {
            Id = "strawberry", Name = "草莓奶油", Motif = "兔耳相机 · 野餐格纹", StickerFile = "CameraMascot.png",
            Paper = Rgb(0xFFF9F5), Ink = Rgb(0x462C39), Muted = Rgb(0x755965), Line = Rgb(0xEAD2D5), Rail = Rgb(0xF6E6E9),
            Accent = Rgb(0x923653), Tint = Rgb(0xFFE8ED), PrimaryFill = Rgb(0xFFB5C5),
            Secondary = Rgb(0x70457F), SecondaryTint = Rgb(0xF5EAF9), SecondaryFill = Rgb(0xE8CBEF),
            Note = Rgb(0xFFF2D8), NoteInk = Rgb(0x815333), NoteLine = Rgb(0xE9D4AA),
            Success = Rgb(0x32674C), SuccessFill = Rgb(0xD5F0DB), OffTrack = Rgb(0xB9A9AF),
            CoverStart = Rgb(0xFFF0DB), CoverEnd = Rgb(0xFFD0DF), Pattern = Rgb(0xDE7290)
        };
        internal static readonly StudioSkin SeaSalt = new StudioSkin {
            Id = "sea-salt", Name = "薄荷海盐", Motif = "海獭相机 · 海浪气泡", StickerFile = "SeaOtter.png",
            Paper = Rgb(0xF1FAF7), Ink = Rgb(0x203F43), Muted = Rgb(0x496665), Line = Rgb(0xC8DED8), Rail = Rgb(0xDDEFE9),
            Accent = Rgb(0x176550), Tint = Rgb(0xDFF5EA), PrimaryFill = Rgb(0x9DE1C5),
            Secondary = Rgb(0x2C617B), SecondaryTint = Rgb(0xE1F2FA), SecondaryFill = Rgb(0xADDCEF),
            Note = Rgb(0xE1F4F5), NoteInk = Rgb(0x316675), NoteLine = Rgb(0xBBDDDC),
            Success = Rgb(0x24604B), SuccessFill = Rgb(0xD1F0D7), OffTrack = Rgb(0xA2B8B2),
            CoverStart = Rgb(0xE7F8E5), CoverEnd = Rgb(0xBCE9EE), Pattern = Rgb(0x50AEB0)
        };
        internal static readonly StudioSkin[] All = { Strawberry, SeaSalt };
        internal static StudioSkin Current = Strawberry;

        internal static StudioSkin Find(string id)
        {
            foreach (StudioSkin skin in All) if (skin.Id == id) return skin;
            return Strawberry;
        }
    }

    // A native menu item supplies arrow-key navigation, dismissal and its accessible checked state.
    internal sealed class SkinMenuItem : ToolStripMenuItem
    {
        internal readonly StudioSkin Skin;
        internal readonly Bitmap Sticker;
        private float zoom = 1;

        internal SkinMenuItem(StudioSkin skin) : base(skin.Name)
        {
            Skin = skin;
            AccessibleDescription = skin.Motif;
            AutoSize = false;
            Margin = Padding.Empty;
            using (Stream stream = typeof(StudioSkin).Assembly.GetManifestResourceStream("ScreenStudio." + skin.StickerFile))
                if (stream != null)
                    using (var image = new Bitmap(stream)) Sticker = new Bitmap(image);
        }

        internal void SizeForDpi(float scale)
        {
            zoom = scale;
            Size = new Size((int)Math.Round(237 * scale), (int)Math.Round(58 * scale));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            GraphicsState state = g.Save();
            Art.Init(g, zoom);
            float w = Width / zoom, h = Height / zoom;
            g.Clear(Color.White);
            Art.Box(g, 1, 1, w-2, h-2, 11, Selected || Checked ? Skin.Tint : Color.White,
                    Selected ? Skin.Accent : Color.Transparent);
            Art.DrawCover(g, new Rectangle(7, 6, 63, 46), Skin, Sticker, true);
            Art.Label(g, Skin.Name, 81, 6, w-112, 25, 15.5f, Skin.Ink, true, StringAlignment.Near);
            Art.Label(g, Skin.Motif, 81, 31, w-101, 19, 11.5f, Skin.Muted, false, StringAlignment.Near);
            if (Checked)
            {
                Art.Dot(g, Skin.Accent, w-27, (h-16)/2, 16);
                Art.Icon(g, "check", w-26, (h-14)/2, 14, Color.White);
            }
            g.Restore(state);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Sticker != null) Sticker.Dispose();
            base.Dispose(disposing);
        }
    }
}
