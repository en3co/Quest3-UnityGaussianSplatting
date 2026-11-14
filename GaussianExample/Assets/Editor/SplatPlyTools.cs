using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

// Binary little-endian Gaussian PLY tools. Subsample keeps a random subset.
// Merge either concatenates two files or exports two scene objects in world space.
// The existing Create GaussianSplatAsset window turns the PLY into an asset.
public static class SplatPlyTools
{
    public struct Header
    {
        public int VertexCount;
        public int Stride;
        public List<string> Properties;
        public long DataOffset;
    }

    public static Header ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var properties = new List<string>();
        var stride = 0;
        var count = 0;
        var binary = false;
        string line;
        while ((line = ReadLine(fs)) != null)
        {
            if (line == "end_header")
                break;
            var tokens = line.Split(' ');
            if (tokens.Length >= 3 && tokens[0] == "format")
                binary = tokens[1] == "binary_little_endian" && tokens[2] == "1.0";
            else if (tokens.Length == 3 && tokens[0] == "element" && tokens[1] == "vertex")
                count = int.Parse(tokens[2]);
            else if (tokens.Length == 3 && tokens[0] == "property")
            {
                stride += tokens[1] switch
                {
                    "float" => 4,
                    "double" => 8,
                    "uchar" => 1,
                    _ => throw new InvalidDataException("Unsupported PLY property type: " + tokens[1])
                };
                properties.Add(line);
            }
        }

        if (!binary)
            throw new InvalidDataException("PLY must be binary little-endian 1.0: " + path);
        if (count <= 0 || stride <= 0 || properties.Count == 0)
            throw new InvalidDataException("PLY has no vertex data: " + path);

        return new Header
        {
            VertexCount = count,
            Stride = stride,
            Properties = properties,
            DataOffset = fs.Position
        };
    }

    public static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        path = path.Trim().Trim('"');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~")
            path = home;
        else if (path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(home, path.Substring(2));
        return path;
    }

    public static void Subsample(string input, string output, int target, int seed)
    {
        input = ExpandPath(input);
        output = ExpandPath(output);
        var header = ReadHeader(input);
        var folder = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(folder))
            throw new InvalidDataException("Output needs a folder and a .ply filename.");
        Directory.CreateDirectory(folder);
        if (target >= header.VertexCount)
        {
            File.Copy(input, output, true);
            return;
        }
        if (target < 1)
            throw new ArgumentOutOfRangeException(nameof(target));

        var indices = new int[header.VertexCount];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;
        var random = new System.Random(seed);
        for (var i = 0; i < target; i++)
        {
            var swap = i + random.Next(header.VertexCount - i);
            (indices[i], indices[swap]) = (indices[swap], indices[i]);
        }

        var chosen = new int[target];
        Array.Copy(indices, chosen, target);
        Array.Sort(chosen);

        using var src = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write);
        WriteHeader(dst, header.Properties, target);
        var record = new byte[header.Stride];
        foreach (var index in chosen)
        {
            src.Position = header.DataOffset + (long)index * header.Stride;
            ReadExact(src, record);
            dst.Write(record, 0, record.Length);
        }
    }

    public static int MergePlaced(GaussianSplatRenderer first, GaussianSplatRenderer second, string output)
    {
        var dir = Path.Combine(Path.GetTempPath(), "splat-ply-merge");
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "first.ply");
        var b = Path.Combine(dir, "second.ply");
        try
        {
            ExportPlaced(first, a);
            ExportPlaced(second, b);
            return Merge(a, b, output);
        }
        finally
        {
            if (File.Exists(a)) File.Delete(a);
            if (File.Exists(b)) File.Delete(b);
        }
    }

    public static void ExportPlaced(GaussianSplatRenderer renderer, string output)
    {
        if (renderer == null)
            throw new InvalidOperationException("Assign both splat objects.");
        if (renderer.asset == null)
            throw new InvalidOperationException(renderer.name + " has no splat asset.");

        var count = renderer.splatCount;
        if (count <= 0)
            throw new InvalidOperationException(renderer.name + " has no splats. Enable it in the open scene and wait for it to draw.");

        var stride = UnsafeUtility.SizeOf<InputSplatData>();
        using var gpu = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        if (!renderer.EditExportData(gpu, true))
            throw new InvalidOperationException(renderer.name + " is not ready to read. Enable it in the open scene.");

        var data = new InputSplatData[count];
        gpu.GetData(data);

        var deleted = Array.Empty<uint>();
        var deletedBuffer = renderer.GpuEditDeleted;
        if (deletedBuffer != null)
        {
            deleted = new uint[deletedBuffer.count];
            deletedBuffer.GetData(deleted);
        }

        var alive = new List<int>(count);
        for (var i = 0; i < data.Length; i++)
        {
            var word = i >> 5;
            var bit = i & 31;
            var isDeleted = word < deleted.Length && (deleted[word] & (1u << bit)) != 0;
            if (!isDeleted && data[i].nor.sqrMagnitude <= 0f)
                alive.Add(i);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        using var fs = new FileStream(output, FileMode.Create, FileAccess.Write);
        var header = "ply\nformat binary_little_endian 1.0\nelement vertex " + alive.Count +
                     "\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz" +
                     "\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2";
        for (var i = 0; i < 45; i++)
            header += "\nproperty float f_rest_" + i;
        header += "\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2" +
                  "\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
        fs.Write(Encoding.ASCII.GetBytes(header));
        var bytes = new byte[stride];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var start = pin.AddrOfPinnedObject();
            foreach (var index in alive)
            {
                System.Runtime.InteropServices.Marshal.Copy(start + index * stride, bytes, 0, stride);
                fs.Write(bytes, 0, stride);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    public static int Merge(string first, string second, string output)
    {
        first = ExpandPath(first);
        second = ExpandPath(second);
        output = ExpandPath(output);
        var a = ReadHeader(first);
        var b = ReadHeader(second);
        if (a.Stride != b.Stride || a.Properties.Count != b.Properties.Count)
            throw new InvalidDataException("PLY layouts do not match.");
        for (var i = 0; i < a.Properties.Count; i++)
        {
            if (a.Properties[i] != b.Properties[i])
                throw new InvalidDataException("PLY property mismatch: " + a.Properties[i] + " vs " + b.Properties[i]);
        }

        var total = a.VertexCount + b.VertexCount;
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write);
        WriteHeader(dst, a.Properties, total);
        AppendVertices(first, a, dst);
        AppendVertices(second, b, dst);
        return total;
    }

    static void AppendVertices(string path, Header header, Stream dst)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        src.Position = header.DataOffset;
        var left = (long)header.VertexCount * header.Stride;
        var buffer = new byte[1024 * 1024];
        while (left > 0)
        {
            var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0)
                throw new EndOfStreamException(path);
            dst.Write(buffer, 0, n);
            left -= n;
        }
    }

    static void WriteHeader(Stream dst, List<string> properties, int count)
    {
        var sb = new StringBuilder();
        sb.Append("ply\nformat binary_little_endian 1.0\nelement vertex ").Append(count).Append('\n');
        foreach (var property in properties)
            sb.Append(property).Append('\n');
        sb.Append("end_header\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        dst.Write(bytes, 0, bytes.Length);
    }

    static string ReadLine(Stream fs)
    {
        var bytes = new List<byte>(64);
        var b = fs.ReadByte();
        if (b < 0)
            return null;
        while (b >= 0 && b != '\n')
        {
            if (b != '\r')
                bytes.Add((byte)b);
            b = fs.ReadByte();
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    static void ReadExact(Stream src, byte[] record)
    {
        var got = 0;
        while (got < record.Length)
        {
            var n = src.Read(record, got, record.Length - got);
            if (n <= 0)
                throw new EndOfStreamException();
            got += n;
        }
    }
}

static class SplatPlyToolGui
{
    public const string Folder = "Assets/GaussianAssets";

    public static string FileRow(string label, string path, bool save)
    {
        EditorGUILayout.BeginHorizontal();
        path = EditorGUILayout.TextField(label, path);
        if (GUILayout.Button("…", GUILayout.Width(24)))
        {
            var expanded = SplatPlyTools.ExpandPath(path);
            var start = string.IsNullOrEmpty(path) ? Folder : Path.GetDirectoryName(expanded);
            var picked = save
                ? EditorUtility.SaveFilePanel(label, start, Path.GetFileName(expanded), "ply")
                : EditorUtility.OpenFilePanel(label, start, "ply");
            if (!string.IsNullOrEmpty(picked))
                path = picked;
        }
        EditorGUILayout.EndHorizontal();
        return path;
    }

    public static string EnsurePlyFile(string output, string input, int count)
    {
        output = SplatPlyTools.ExpandPath(output);
        if (string.IsNullOrEmpty(Path.GetExtension(output)) || Directory.Exists(output))
            output = Path.Combine(output, Path.GetFileName(DefaultOutput(input, count)));
        return output;
    }

    public static bool Ready(string input, string output)
    {
        return File.Exists(SplatPlyTools.ExpandPath(input)) && !string.IsNullOrWhiteSpace(output);
    }

    public static string DefaultOutput(string input, int count)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        if (string.IsNullOrEmpty(name))
            name = "splat";
        return Path.Combine(Folder, name + "_" + count + ".ply").Replace('\\', '/');
    }

    public static void Status(string status)
    {
        if (!string.IsNullOrEmpty(status))
            EditorGUILayout.HelpBox(status, MessageType.Info);
        EditorGUILayout.HelpBox("The result is a binary PLY. Pack it with Tools/Gaussian Splats/Create GaussianSplatAsset.", MessageType.None);
    }
}

public class SplatSubsampleWindow : EditorWindow
{
    string m_Input = "";
    string m_Output = "";
    int m_Count;
    int m_Target = 50000;
    int m_Seed = 1;
    string m_Status = "";

    [MenuItem("Tools/Gaussian Splats/Subsample PLY")]
    public static void Open()
    {
        GetWindow<SplatSubsampleWindow>("Subsample PLY");
    }

    void OnGUI()
    {
        m_Input = SplatPlyToolGui.FileRow("Input PLY", m_Input, false);
        if (GUILayout.Button("Read count") && File.Exists(SplatPlyTools.ExpandPath(m_Input)))
        {
            m_Input = SplatPlyTools.ExpandPath(m_Input);
            var header = SplatPlyTools.ReadHeader(m_Input);
            m_Count = header.VertexCount;
            m_Target = Mathf.Clamp(m_Target, 1, m_Count);
            if (OutputFollowsKeep(m_Output, m_Target))
                m_Output = SplatPlyToolGui.DefaultOutput(m_Input, m_Target);
            m_Status = m_Count.ToString("N0") + " splats";
        }

        var previousTarget = m_Target;
        if (m_Count > 1)
            m_Target = EditorGUILayout.IntSlider("Keep", m_Target, 1, m_Count);
        else
            m_Target = EditorGUILayout.IntField("Keep", Mathf.Max(1, m_Target));
        if (m_Count > 0 && m_Target != previousTarget && OutputFollowsKeep(m_Output, previousTarget))
            m_Output = SplatPlyToolGui.DefaultOutput(m_Input, m_Target);
        m_Seed = EditorGUILayout.IntField("Seed", m_Seed);
        m_Output = SplatPlyToolGui.FileRow("Output PLY", m_Output, true);
        if (GUILayout.Button("Write subsample") && SplatPlyToolGui.Ready(m_Input, m_Output))
        {
            try
            {
                var output = SplatPlyToolGui.EnsurePlyFile(m_Output, m_Input, m_Target);
                SplatPlyTools.Subsample(m_Input, output, m_Target, m_Seed);
                m_Output = output;
                AssetDatabase.Refresh();
                m_Status = "Wrote " + output;
            }
            catch (Exception ex)
            {
                m_Status = ex.Message;
            }
        }

        SplatPlyToolGui.Status(m_Status);
    }

    bool OutputFollowsKeep(string output, int count)
    {
        return string.IsNullOrEmpty(output) || output == SplatPlyToolGui.DefaultOutput(m_Input, count);
    }
}

public class SplatMergeWindow : EditorWindow
{
    string m_First = "";
    string m_Second = "";
    string m_Output = "";
    GaussianSplatRenderer m_FirstObject;
    GaussianSplatRenderer m_SecondObject;
    bool m_UsePlacement = true;
    bool m_ThenSubsample;
    int m_Keep = 50000;
    int m_Seed = 1;
    string m_Status = "";

    [MenuItem("Tools/Gaussian Splats/Merge PLY")]
    public static void Open()
    {
        GetWindow<SplatMergeWindow>("Merge PLY");
    }

    void OnGUI()
    {
        m_UsePlacement = EditorGUILayout.Toggle("Use scene placement", m_UsePlacement);
        if (m_UsePlacement)
        {
            EditorGUILayout.HelpBox("Arrange the two splats in the Scene for where they will be in the merged file.", MessageType.None);
            m_FirstObject = (GaussianSplatRenderer)EditorGUILayout.ObjectField("First object", m_FirstObject, typeof(GaussianSplatRenderer), true);
            m_SecondObject = (GaussianSplatRenderer)EditorGUILayout.ObjectField("Second object", m_SecondObject, typeof(GaussianSplatRenderer), true);
        }
        else
        {
            m_First = SplatPlyToolGui.FileRow("First PLY filepath", m_First, false);
            m_Second = SplatPlyToolGui.FileRow("Second PLY filepath", m_Second, false);
        }
        m_Output = SplatPlyToolGui.FileRow("Merged PLY filepath", m_Output, true);
        m_ThenSubsample = EditorGUILayout.Toggle("Subsample after merge", m_ThenSubsample);
        if (m_ThenSubsample)
        {
            m_Keep = EditorGUILayout.IntField("Keep after merge", m_Keep);
            m_Seed = EditorGUILayout.IntField("Seed", m_Seed);
        }

        var ready = !string.IsNullOrWhiteSpace(m_Output) && (m_UsePlacement
            ? m_FirstObject != null && m_SecondObject != null
            : SplatPlyToolGui.Ready(m_First, m_Output) && File.Exists(SplatPlyTools.ExpandPath(m_Second)));
        if (GUILayout.Button("Merge") && ready)
        {
            try
            {
                var nameSource = m_UsePlacement ? m_FirstObject.name + ".ply" : m_First;
                var output = SplatPlyToolGui.EnsurePlyFile(m_Output, nameSource, m_Keep);
                var total = m_UsePlacement
                    ? SplatPlyTools.MergePlaced(m_FirstObject, m_SecondObject, output)
                    : SplatPlyTools.Merge(m_First, m_Second, output);
                if (m_ThenSubsample && m_Keep < total)
                {
                    var temp = output + ".full.ply";
                    File.Move(output, temp);
                    SplatPlyTools.Subsample(temp, output, m_Keep, m_Seed);
                    File.Delete(temp);
                    m_Status = "Merged " + total.ToString("N0") + " then kept " + m_Keep.ToString("N0") + "\n" + output;
                }
                else
                    m_Status = "Merged " + total.ToString("N0") + " splats\n" + output;
                m_Output = output;
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                m_Status = ex.Message;
            }
        }

        SplatPlyToolGui.Status(m_Status);
    }
}
