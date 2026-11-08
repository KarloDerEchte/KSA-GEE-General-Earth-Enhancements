using System.Text;

namespace GEE.Tests;

/// <summary>Minimal Mapbox Vector Tile writer, enough to feed VectorTile.Parse.</summary>
internal sealed class MvtBuilder
{
    private readonly List<byte[]> _layers = new();

    public sealed class Layer(string name, int extent = 4096)
    {
        public readonly string Name = name;
        public readonly int Extent = extent;
        public readonly List<string> Keys = new();
        public readonly List<byte[]> Values = new();
        public readonly List<byte[]> Features = new();

        private int Key(string k) { int i = Keys.IndexOf(k); if (i < 0) { Keys.Add(k); i = Keys.Count - 1; } return i; }

        private int Value(object v)
        {
            var b = new Buf();
            switch (v)
            {
                case string s: b.Tag(1, 2); b.Bytes(Encoding.UTF8.GetBytes(s)); break;
                case double d: b.Tag(3, 1); b.Raw(BitConverter.GetBytes(d)); break;
                case int n: b.Tag(5, 0); b.Varint((ulong)n); break;
                case bool f: b.Tag(7, 0); b.Varint(f ? 1UL : 0UL); break;
                default: throw new ArgumentException(v.GetType().Name);
            }
            byte[] bytes = b.ToArray();
            for (int i = 0; i < Values.Count; i++)
                if (Values[i].AsSpan().SequenceEqual(bytes)) return i;
            Values.Add(bytes);
            return Values.Count - 1;
        }

        /// <summary>type: 1 point, 2 line, 3 polygon. Each part is a list of tile-coordinate points.</summary>
        public Layer Add(long id, int type, IEnumerable<IReadOnlyList<(int X, int Y)>> parts, params (string Key, object Value)[] tags)
        {
            var geometry = new List<uint>();
            int cx = 0, cy = 0;
            foreach (var part in parts)
            {
                geometry.Add(1 | 1u << 3);
                geometry.Add(Zig(part[0].X - cx)); geometry.Add(Zig(part[0].Y - cy));
                cx = part[0].X; cy = part[0].Y;
                if (part.Count > 1)
                {
                    geometry.Add(2 | (uint)(part.Count - 1) << 3);
                    for (int i = 1; i < part.Count; i++)
                    {
                        geometry.Add(Zig(part[i].X - cx)); geometry.Add(Zig(part[i].Y - cy));
                        cx = part[i].X; cy = part[i].Y;
                    }
                }
                if (type == 3)
                    geometry.Add(7 | 1u << 3);
            }
            var tagIdx = new List<uint>();
            foreach (var (k, v) in tags) { tagIdx.Add((uint)Key(k)); tagIdx.Add((uint)Value(v)); }
            var f = new Buf();
            f.Tag(1, 0); f.Varint((ulong)id);
            f.Tag(2, 2); f.Bytes(Packed(tagIdx));
            f.Tag(3, 0); f.Varint((ulong)type);
            f.Tag(4, 2); f.Bytes(Packed(geometry));
            Features.Add(f.ToArray());
            return this;
        }

        public Layer Polygon(long id, IReadOnlyList<(int X, int Y)> ring, params (string, object)[] tags) => Add(id, 3, new[] { ring }, tags);
        public Layer Line(long id, IReadOnlyList<(int X, int Y)> line, params (string, object)[] tags) => Add(id, 2, new[] { line }, tags);

        public byte[] Encode()
        {
            var l = new Buf();
            l.Tag(1, 2); l.Bytes(Encoding.UTF8.GetBytes(Name));
            foreach (var f in Features) { l.Tag(2, 2); l.Bytes(f); }
            foreach (var k in Keys) { l.Tag(3, 2); l.Bytes(Encoding.UTF8.GetBytes(k)); }
            foreach (var v in Values) { l.Tag(4, 2); l.Bytes(v); }
            l.Tag(5, 0); l.Varint((ulong)Extent);
            l.Tag(15, 0); l.Varint(2);
            return l.ToArray();
        }

        private static byte[] Packed(IEnumerable<uint> values)
        {
            var b = new Buf();
            foreach (uint v in values) b.Varint(v);
            return b.ToArray();
        }
    }

    public Layer AddLayer(string name, int extent = 4096)
    {
        var layer = new Layer(name, extent);
        _pending.Add(layer);
        return layer;
    }

    private readonly List<Layer> _pending = new();

    public byte[] Build()
    {
        var t = new Buf();
        foreach (var l in _pending) { t.Tag(3, 2); t.Bytes(l.Encode()); }
        return t.ToArray();
    }

    public static uint Zig(int v) => (uint)((v << 1) ^ (v >> 31));

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void Varint(ulong v) { while (v >= 0x80) { _b.Add((byte)(v | 0x80)); v >>= 7; } _b.Add((byte)v); }
        public void Tag(int field, int wire) => Varint((ulong)(field << 3 | wire));
        public void Bytes(byte[] data) { Varint((ulong)data.Length); _b.AddRange(data); }
        public void Raw(byte[] data) => _b.AddRange(data);
        public byte[] ToArray() => _b.ToArray();
    }
}
