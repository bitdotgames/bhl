using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.IO;

namespace bhl {

public class UserError : Exception
{
  public string file;

  public UserError(string file, string str)
    : base(str)
  {
    this.file = file;
  }

  public UserError(string str)
    : base(str)
  {
    this.file = null;
  }

  public string ToJson()
  {
    var msg = Message.Replace("\\", " ");
    msg = msg.Replace("\n", " ");
    msg = msg.Replace("\r", " ");
    msg = msg.Replace("\"", "\\\""); 
    return "{\"error\": \"" + msg + "\", \"file\": \"" + (file == null ? "<?>" : file.Replace("\\", "/")) + "\"}";
  }
}

public static class Hash
{
  static public uint CRC32(string id)
  {
    var bytes = System.Text.Encoding.ASCII.GetBytes(id);
    return Crc32.Compute(bytes, bytes.Length);
  }

  static public uint CRC28(string id)
  {
    return CRC32(id) & 0xFFFFFFF;
  }

  static public uint CRC20(string id)
  {
    return CRC32(id) & 0xFFFFF;
  }

  static public uint CRC32(byte[] bytes)
  {
    return Crc32.Compute(bytes, bytes.Length);
  }

  static public uint CRC28(byte[] bytes)
  {
    return CRC32(bytes) & 0xFFFFFFF;
  }

  static public uint CRC20(byte[] bytes)
  {
    return CRC32(bytes) & 0xFFFFF;
  }
}

public struct HashedName
{
  public ulong n;
  public string s;

  public uint n1 {
    get {
      return (uint)(n & 0xFFFFFFFF);
    }
  }

  public uint n2 {
    get {
      return (uint)(n >> 32);
    }
  }

  public HashedName(ulong n, string s = "")
  {
    this.n = n;
    this.s = s;
  }

  public HashedName(string s)
    : this(Hash.CRC28(s), s)
  {}

  public HashedName(uint n1, uint n2, string s = "")
    : this(((ulong)n2 << 32) | ((ulong)n1), s)
  {}

  public HashedName(string n1, uint n2)
    : this(Hash.CRC28(n1), n2, n1)
  {}

  static public void Split(ulong n, out uint n1, out uint n2)
  {
    n1 = (uint)(n & 0xFFFFFFFF);
    n2 = (uint)(n >> 32);
  }

  public void Split(out uint n1, out uint n2)
  {
    n1 = (uint)(n & 0xFFFFFFFF);
    n2 = (uint)(n >> 32);
  }

  public static implicit operator HashedName(ulong n)
  {
    return new HashedName(n);
  }

  public static implicit operator HashedName(uint n)
  {
    return new HashedName((ulong)n);
  }

  public static implicit operator HashedName(string s)
  {
    return new HashedName(s);
  }

  public bool IsEmpty()
  {
    return n == 0;
  }

  public override string ToString() 
  {
    return (s == null ? "" : s) + ":" + n;
  }

  public bool IsEqual(HashedName o)
  {
    return n == o.n;
  }
}

static public class OPool
{
  public struct PoolItem
  {
    public bool used;
    public object obj;
  }

  static public List<PoolItem> pool = new List<PoolItem>();
  static public int hits = 0;
  static public int miss = 0;

  static public T Request<T>() where T : class, new()
  {
    for(int i=0;i<pool.Count;++i)
    {
      var item = pool[i];
      if(!item.used && item.obj is T)
      {
        ++hits;

        var res = item.obj as T;
        item.used = true;
        pool[i] = item;
        return res;
      }
    }

    {
      var res = new T();
      AddToPool(res);
      return res;
    }
  }

  static void AddToPool(object obj)
  {
    ++miss;

    var item = new PoolItem();
    item.used = true;
    item.obj = obj;
    pool.Add(item);
  }

  static public void Release(object obj)
  {
    if(obj == null)
      return;

    for(int i=0;i<pool.Count;++i)
    {
      var item = pool[i];
      if(item.obj == obj)
      {
        item.used = false;
        pool[i] = item;
        break;
      }
    }
  }

  static public void Clear()
  {
    hits = 0;
    miss = 0;
    pool.Clear();
  }
}

public static class Extensions
{
  public static void Append(this AST dst, AST src)
  {
    for(int i=0;i<src.children.Count;++i)
    {
      dst.children.Add(src.children[i]);
    }
  }

  public static Stream ToStream(this string str)
  {
    MemoryStream stream = new MemoryStream();
    StreamWriter writer = new StreamWriter(stream);
    writer.Write(str);
    writer.Flush();
    stream.Position = 0;
    return stream;
  }

  public static void SetData(this MemoryStream ms, byte[] b, int offset, int blen)
  {
    if(ms.Capacity < b.Length)
      ms.Capacity = b.Length;

    ms.SetLength(0);
    ms.Write(b, offset, blen);
    ms.Position = 0;
  }

  public static int CopyTo(this Stream src, Stream dest)
  {
    int size = (src.CanSeek) ? Math.Min((int)(src.Length - src.Position), 0x2000) : 0x2000;
    byte[] buffer = new byte[size];
    int total = 0;
    int n;
    do
    {
      n = src.Read(buffer, 0, buffer.Length);
      dest.Write(buffer, 0, n);
      total += n;
    } while (n != 0);           
    return total;
  }
}

static public class Util
{
  static public bool DEBUG = false;

  public delegate void LogCb(string text);
  static public LogCb Debug = DefaultDebug;
  static public LogCb Error = DefaultError;

  static public void DefaultDebug(string str)
  {
    Console.WriteLine("[DBG] " + str);
  }

  static public void DefaultError(string str)
  {
    Console.WriteLine("[ERR] " + str);
  }
  ////////////////////////////////////////////////////////

  public static void Verify(bool condition)
  {
    if(!condition) throw new Exception();
  }

  public static void Verify(bool condition, string fmt, params object[] vals)
  {
    if(!condition) throw new Exception(string.Format(fmt, vals));
  }

  public static void Verify(bool condition, string msg)
  {
    if(!condition) throw new Exception(msg);
  }

  ////////////////////////////////////////////////////////

  static public string NormalizeFilePath(string file_path)
  {
    return Path.GetFullPath(file_path).Replace("\\", "/");
  }

  static public string ResolveImportPath(List<string> inc_paths, string self_path, string path)
  {
    //relative import
    if(!Path.IsPathRooted(path))
    {
      var dir = Path.GetDirectoryName(self_path);
      return Path.GetFullPath(Path.Combine(dir, path) + ".bhl");
    }
    //absolute import
    else
      return TryIncludePaths(inc_paths, path);
  }

  static string TryIncludePaths(List<string> inc_paths, string path)
  {
    for(int i=0;i<inc_paths.Count;++i)
    {
      var file_path = Path.GetFullPath(inc_paths[i] + "/" + path  + ".bhl");
      if(File.Exists(file_path))
        return file_path;
    }
    throw new Exception("Could not find file for path '" + path + "'");
  }

  ////////////////////////////////////////////////////////

  static public T File2Meta<T>(string file) where T : IMetaStruct, new()
  {
    using(FileStream rfs = File.Open(file, FileMode.Open, FileAccess.Read))
    {
      return Bin2Meta<T>(rfs);
    }
  }

  static MetaHelper.CreateByIdCb prev_create_factory; 

  static public void SetupASTFactory()
  {
    prev_create_factory = MetaHelper.CreateById;
    MetaHelper.CreateById = AST_Factory.createById;
  }

  static public void RestoreASTFactory()
  {
    MetaHelper.CreateById = prev_create_factory;
  }

  static public T Bin2Meta<T>(Stream s) where T : IMetaStruct, new()
  {
    var reader = new MsgPackDataReader(s);
    var ast = new T();
    var err = MetaHelper.syncSafe(MetaSyncContext.NewForRead(reader), ref ast);

    if(err != MetaIoError.SUCCESS)
      throw new Exception("Could not read: " + err);

    return ast;
  }

  static public T Bin2Meta<T>(byte[] bytes) where T : IMetaStruct, new()
  {
    return Bin2Meta<T>(new MemoryStream(bytes));
  }

  static public void Meta2Bin<T>(T ast, Stream dst) where T : IMetaStruct
  {
    var writer = new MsgPackDataWriter(dst);
    var err = MetaHelper.syncSafe(MetaSyncContext.NewForWrite(writer), ref ast);

    if(err != MetaIoError.SUCCESS)
      throw new Exception("Could not write: " + err);
  }

  static public void Meta2File<T>(T ast, string file) where T : IMetaStruct
  {
    using(FileStream wfs = new FileStream(file, FileMode.Create, System.IO.FileAccess.Write))
    {
      Meta2Bin(ast, wfs);
    }
  }

  static public HashedName GetFuncId(uint mod_id, string name)
  {
    return new HashedName(name, mod_id);
  }

  static public HashedName GetFuncId(uint mod_id, uint name)
  {
    return new HashedName(name, mod_id);
  }

  static public HashedName GetFuncId(string module, string name)
  {
    return GetFuncId(Hash.CRC32(module), name);
  }

  static public HashedName GetModuleId(string module)
  {
    return new HashedName(Hash.CRC32(module), module);
  }

  public static void ASTDump(AST ast)
  {
    new AST_Dumper().Visit(ast);
    Console.WriteLine("\n=============");
  }

  public static void NodeDump(BehaviorTreeNode node, bool only_running = false, int level = 0, bool is_term = true)
  {
    var fnode = node as FuncNodeAST;
    if(fnode != null)
      fnode.Inflate();

    var name = node.GetType().Name;
    var status = node.currStatus;

    var spaces = new String('_', level);
    var indent_str = spaces + (is_term ? "`" : "|");
    Console.WriteLine(indent_str + "-" + name + " (" + status + ") " + node.inspect());

    if(!only_running || (only_running && status == BHS.RUNNING))
    {
      var inode = node as BehaviorTreeInternalNode;
      if(inode != null)
      {
        for(int i=0;i<inode.children.Count;++i)
        {
          var child = inode.children[i]; 
          NodeDump(child, only_running, level + 1, i == inode.children.Count-1);
        }
      }
    }
  }
}

public struct FuncArgsInfo
{
  //NOTE: 6 bits are used for a total number of args passed (max 63), 
  //      26 bits are reserved for default args set bits(max 26 default args)
  const int ARGS_NUM_BITS = 6;
  const uint ARGS_NUM_MASK = ((1 << ARGS_NUM_BITS) - 1);
  const int MAX_ARGS = (int)ARGS_NUM_MASK;
  const int MAX_DEFAULT_ARGS = 32 - ARGS_NUM_BITS; 

  public uint bits;

  public FuncArgsInfo(uint bits)
  {
    this.bits = bits;
  }

  public int CountArgs()
  {
    return (int)(bits & ARGS_NUM_MASK);
  }

  public bool SetArgsNum(int num)
  {
    if(num > MAX_ARGS)
      return false;
    bits = (bits & ~ARGS_NUM_MASK) | (uint)num;
    return true;
  }

  public bool IncArgsNum()
  {
    uint num = bits & ARGS_NUM_MASK; 
    ++num;
    if(num > MAX_ARGS)
      return false;
    bits = (bits & ~ARGS_NUM_MASK) | num;
    return true;
  }

  //NOTE: idx starts from 0, it's the idx of the default argument *within* default arguments,
  //      e.g: func Foo(int a, int b = 1, int c = 2) { .. }  
  //           b is 0 default arg idx, c is 1 
  public bool UseDefaultArg(int idx, bool flag)
  {
    if(idx >= MAX_DEFAULT_ARGS)
      return false;
    uint mask = 1u << (idx + ARGS_NUM_BITS); 
    if(flag)
      bits |= mask;
    else
      bits &= ~mask;
    return true;
  }

  //NOTE: idx starts from 0
  public bool IsDefaultArgUsed(int idx)
  {
    return (bits & (1u << (idx + ARGS_NUM_BITS))) != 0;
  }
}

static public class AST_Util
{
  static public List<AST_Base> GetChildren(this AST_Base self)
  {
    var ast = self as AST;
    return ast == null ? null : ast.children;
  }

  static public void AddChild(this AST_Base self, AST_Base c)
  {
    if(c == null)
      return;
    var ast = self as AST;
    ast.children.Add(c);
  }

  static public void AddChild(this AST self, AST_Base c)
  {
    if(c == null)
      return;
    self.children.Add(c);
  }

  static public AST NewInterimChild(this AST self)
  {
    var c = new AST_Interim();
    self.AddChild(c);
    return c;
  }

  ////////////////////////////////////////////////////////

  static public AST_Module New_Module(uint nname, string name)
  {
    var n = new AST_Module();
    n.nname = nname;
    if(Util.DEBUG)
      n.name = name;
    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_Import New_Imports()
  {
    var n = new AST_Import();
    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_FuncDecl New_FuncDecl(HashedName name, HashedName type)
  {
    var n = new AST_FuncDecl();
    Init_FuncDecl(n, name, type);
    return n;
  }

  static void Init_FuncDecl(AST_FuncDecl n, HashedName name, HashedName type)
  {
    n.ntype = (uint)type.n; 
    if(Util.DEBUG)
      n.type = type.s;

    n.nname1 = name.n1;
    //module id
    n.nname2 = name.n2;

    if(Util.DEBUG)
      n.name = name.s;

    //fparams
    n.NewInterimChild();
    //block
    n.NewInterimChild();
  }

  static public AST fparams(this AST_FuncDecl n)
  {
    return n.GetChildren()[0] as AST;
  }

  static public AST block(this AST_FuncDecl n)
  {
    return n.GetChildren()[1] as AST;
  }

  static public int GetDefaultArgsNum(this AST_FuncDecl n)
  {
    var fparams = n.fparams();
    if(fparams.children.Count == 0)
      return 0;

    int num = 0;
    for(int i=0;i<fparams.children.Count;++i)
    {
      var fc = fparams.children[i].GetChildren();
      if(fc != null && fc.Count > 0)
        ++num;
    }
    return num;
  }

  static public int GetTotalArgsNum(this AST_FuncDecl n)
  {
    var fparams = n.fparams();
    if(fparams.children.Count == 0)
      return 0;
    int num = fparams.children.Count;
    return num;
  }

  static public HashedName Name(this AST_FuncDecl n)
  {
    return (n == null) ? new HashedName(0, "?") : new HashedName(n.nname1, n.nname2, n.name);
  }

  static public ulong nname(this AST_FuncDecl n)
  {
    return ((ulong)n.nname2 << 32) | ((ulong)n.nname1);
  }

  ////////////////////////////////////////////////////////

  static public AST_LambdaDecl New_LambdaDecl(HashedName name, HashedName type)
  {
    var n = new AST_LambdaDecl();
    Init_FuncDecl(n, name, type);

    return n;
  }

  static public HashedName Name(this AST_LambdaDecl n)
  {
    return new HashedName(n.nname1, n.nname2, n.name);
  }

  ////////////////////////////////////////////////////////

  static public AST_ClassDecl New_ClassDecl(HashedName name, HashedName parent)
  {
    var n = new AST_ClassDecl();

    n.nname = (uint)name.n;
    if(Util.DEBUG)
      n.name = name.s;

    n.nparent = (uint)parent.n;
    if(Util.DEBUG)
      n.parent = parent.s;

    return n;
  }

  static public HashedName Name(this AST_ClassDecl n)
  {
    return (n == null) ? new HashedName(0, "?") : new HashedName(n.nname, n.name);
  }

  static public HashedName ParentName(this AST_ClassDecl n)
  {
    return (n == null) ? new HashedName(0, "?") : new HashedName(n.nparent, n.parent);
  }

  ////////////////////////////////////////////////////////

  static public AST_EnumDecl New_EnumDecl(HashedName name)
  {
    var n = new AST_EnumDecl();

    n.nname = (uint)name.n;
    if(Util.DEBUG)
      n.name = name.s;

    return n;
  }

  static public HashedName Name(this AST_EnumDecl n)
  {
    return (n == null) ? new HashedName(0, "?") : new HashedName(n.nname, n.name);
  }

  ////////////////////////////////////////////////////////
  
  static public AST_UnaryOpExp New_UnaryOpExp(EnumUnaryOp type)
  {
    var n = new AST_UnaryOpExp();
    n.type = type;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_BinaryOpExp New_BinaryOpExp(EnumBinaryOp type)
  {
    var n = new AST_BinaryOpExp();
    n.type = type;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_TypeCast New_TypeCast(HashedName type)
  {
    var n = new AST_TypeCast();
    n.ntype = (uint)type.n;
    if(Util.DEBUG)
      n.type = type.s;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_UseParam New_UseParam(HashedName name, bool is_ref)
  {
    var n = new AST_UseParam();
    n.nname = (uint)name.n | (is_ref ? 1u << 29 : 0u);
    if(Util.DEBUG)
      n.name = name.s;

    return n;
  }

  static public bool IsRef(this AST_UseParam n)
  {
    return (n.nname & (1u << 29)) != 0; 
  }

  static public HashedName Name(this AST_UseParam n)
  {
    return new HashedName(n.nname & 0xFFFFFFF, n.name);
  }

  ////////////////////////////////////////////////////////

  static public AST_Call New_Call(EnumCall type, int line_num, HashedName name = new HashedName(), ClassSymbol scope_symb = null)
  {
    return New_Call(type, line_num, name, scope_symb != null ? (uint)scope_symb.Type().n : 0);
  }

  static public AST_Call New_Call(EnumCall type, int line_num, HashedName name, uint scope_ntype)
  {
    var n = new AST_Call();
    n.type = type;
    n.nname1 = name.n1;
    n.nname2 = name.n2;
    if(Util.DEBUG)
      n.name = name.s;
    n.scope_ntype = scope_ntype;
    n.line_num = (uint)line_num;

    return n;
  }

  static public HashedName Name(this AST_Call n)
  {
    return new HashedName(n.nname(), n.name);
  }

  static public ulong nname(this AST_Call n)
  {
    return ((ulong)n.nname2 << 32) | ((ulong)n.nname1);
  }

  static public ulong FuncId(this AST_Call n)
  {
    if(n.nname2 == 0)
      return ((ulong)n.scope_ntype << 32) | ((ulong)n.nname1);
    else
      return ((ulong)n.nname2 << 32) | ((ulong)n.nname1);
  }

  ////////////////////////////////////////////////////////

  static public AST_Return New_Return()
  {
    return new AST_Return();
  }

  static public AST_Break New_Break()
  {
    return new AST_Break();
  }
  
  ////////////////////////////////////////////////////////

  static public AST_PopValue New_PopValue()
  {
    return new AST_PopValue();
  }

  ////////////////////////////////////////////////////////

  static public AST_New New_New(ClassSymbol type)
  {
    var n = new AST_New();
    var type_name = type.Type(); 
    n.ntype = (uint)type_name.n;
    if(Util.DEBUG)
      n.type = type_name.s;

    return n;
  }

  static public HashedName Name(this AST_New n)
  {
    return new HashedName(n.ntype, n.type);
  }

  ////////////////////////////////////////////////////////

  static public AST_Literal New_Literal(EnumLiteral type)
  {
    var n = new AST_Literal();
    n.type = type;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_VarDecl New_VarDecl(HashedName name, bool is_ref, uint ntype)
  {
    var n = new AST_VarDecl();
    n.nname = (uint)name.n | (is_ref ? 1u << 29 : 0u);
    if(Util.DEBUG)
      n.name = name.s;
    n.ntype = ntype;

    return n;
  }

  static public HashedName Name(this AST_VarDecl n)
  {
    return new HashedName(n.nname & 0xFFFFFFF, n.name);
  }

  static public bool IsRef(this AST_VarDecl n)
  {
    return (n.nname & (1u << 29)) != 0; 
  }

  ////////////////////////////////////////////////////////

  static public AST_Inc New_Inc(HashedName name)
  {
    var n = new AST_Inc();
    n.nname = (uint)name.n;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_Block New_Block(EnumBlock type)
  {
    var n = new AST_Block();
    n.type = type;

    return n;
  }

  ////////////////////////////////////////////////////////

  static public AST_JsonObj New_JsonObj(HashedName root_type_name)
  {
    var n = new AST_JsonObj();
    n.ntype = (uint)root_type_name.n;
    return n;
  }

  static public AST_JsonArr New_JsonArr(ArrayTypeSymbol arr_type)
  {
    var n = new AST_JsonArr();
    n.ntype = (uint)arr_type.Type().n;
    return n;
  }

  static public AST_JsonArrAddItem New_JsonArrAddItem()
  {
    var n = new AST_JsonArrAddItem();
    return n;
  }

  static public AST_JsonPair New_JsonPair(HashedName scope_type, HashedName name)
  {
    var n = new AST_JsonPair();
    n.scope_ntype = (uint)scope_type.n;
    n.nname = (uint)name.n;
    if(Util.DEBUG)
      n.name = name.s;

    return n;
  }

  static public HashedName Name(this AST_JsonPair n)
  {
    return new HashedName(n.nname, n.name);
  }
}

/// <summary>
/// Implements a 32-bit CRC hash algorithm compatible with Zip etc.
/// </summary>
/// <remarks>
/// Crc32 should only be used for backward compatibility with older file formats
/// and algorithms. It is not secure enough for new applications.
/// If you need to call multiple times for the same data either use the HashAlgorithm
/// interface or remember that the result of one Compute call needs to be ~ (XOR) before
/// being passed in as the seed for the next Compute call.
/// </remarks>
public sealed class Crc32 : HashAlgorithm
{
  public const UInt32 DefaultPolynomial = 0xedb88320u;
  public const UInt32 DefaultSeed = 0xffffffffu;

  private static UInt32[] defaultTable;

  private readonly UInt32 seed;
  private readonly UInt32[] table;
  private UInt32 hash;

  public Crc32()
    : this(DefaultPolynomial, DefaultSeed)
  {
  }

  public Crc32(UInt32 polynomial, UInt32 seed)
  {
    table = InitializeTable(polynomial);
    this.seed = hash = seed;
  }

  public override void Initialize()
  {
    hash = seed;
  }

  protected override void HashCore(byte[] buffer, int start, int length)
  {
    hash = CalculateHash(table, hash, buffer, start, length);
  }

  protected override byte[] HashFinal()
  {
    var hashBuffer = UInt32ToBigEndianBytes(~hash);
    HashValue = hashBuffer;
    return hashBuffer;
  }

  public override int HashSize { get { return 32; } }

  public static UInt32 Compute(byte[] buffer, int buffer_len)
  {
    return Compute(DefaultSeed, buffer, buffer_len);
  }

  public static UInt32 Compute(UInt32 seed, byte[] buffer, int buffer_len)
  {
    return Compute(DefaultPolynomial, seed, buffer, buffer_len);
  }

  public static UInt32 Compute(UInt32 polynomial, UInt32 seed, byte[] buffer, int buffer_len)
  {
    return ~CalculateHash(InitializeTable(polynomial), seed, buffer, 0, buffer_len);
  }

  private static UInt32[] InitializeTable(UInt32 polynomial)
  {
    if (polynomial == DefaultPolynomial && defaultTable != null)
      return defaultTable;

    var createTable = new UInt32[256];
    for (var i = 0; i < 256; i++)
    {
      var entry = (UInt32)i;
      for (var j = 0; j < 8; j++)
        if ((entry & 1) == 1)
          entry = (entry >> 1) ^ polynomial;
        else
          entry = entry >> 1;
      createTable[i] = entry;
    }

    if (polynomial == DefaultPolynomial)
      defaultTable = createTable;

    return createTable;
  }

  private static UInt32 CalculateHash(UInt32[] table, UInt32 seed, IList<byte> buffer, int start, int size)
  {
    var crc = seed;
    for (var i = start; i < size - start; i++)
      crc = (crc >> 8) ^ table[buffer[i] ^ crc & 0xff];
    return crc;
  }

  public static UInt32 BeginHash()
  {
    var crc = DefaultSeed;
    return crc;
  }

  public static UInt32 AddHash(UInt32 crc, byte data)
  {
    UInt32[] table = InitializeTable(DefaultPolynomial);
    crc = (crc >> 8) ^ table[data ^ crc & 0xff];
    return crc;
  }

  public static UInt32 FinalizeHash(UInt32 crc)
  {
    return ~crc;
  }

  private static byte[] UInt32ToBigEndianBytes(UInt32 uint32)
  {
    var result = BitConverter.GetBytes(uint32);

    if (BitConverter.IsLittleEndian)
      Array.Reverse(result);

    return result;
  }
}

public class AST_Dumper : AST_Visitor
{
  public override void DoVisit(AST_Interim node)
  {
    Console.Write("(");
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Module node)
  {
    Console.Write("(MODULE " + node.nname);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Import node)
  {
    Console.Write("(IMPORT ");
    foreach(var m in node.modules)
      Console.Write("" + m);
    Console.Write(")");
  }

  public override void DoVisit(AST_FuncDecl node)
  {
    Console.Write("(FUNC ");
    Console.Write(node.type + " " + node.name + " " + node.nname());
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_LambdaDecl node)
  {
    Console.Write("(LMBD ");
    Console.Write(node.type + " " + node.nname() + " USE:");
    for(int i=0;i<node.useparams.Count;++i)
      Console.Write(" " + node.useparams[i].nname);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_ClassDecl node)
  {
    Console.Write("(CLASS ");
    Console.Write(node.name + " " + node.nname);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_EnumDecl node)
  {
    Console.Write("(ENUM ");
    Console.Write(node.name + " " + node.nname);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Block node)
  {
    Console.Write("(");
    Console.Write(node.type + " {");
    VisitChildren(node);
    Console.Write("})");
  }

  public override void DoVisit(AST_TypeCast node)
  {
    Console.Write("(CAST ");
    Console.Write(node.type + " ");
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Call node)
  {
    Console.Write("(CALL ");
    Console.Write(node.type + " " + node.name + " " + node.nname());
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Inc node)
  {
    Console.Write("(INC ");
    Console.Write(node.nname);
    Console.Write(")");
  }

  public override void DoVisit(AST_Return node)
  {
    Console.Write("(RET ");
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_Break node)
  {
    Console.Write("(BRK ");
    Console.Write(")");
  }

  public override void DoVisit(AST_PopValue node)
  {
    Console.Write("(POP ");
    Console.Write(")");
  }

  public override void DoVisit(AST_Literal node)
  {
    if(node.type == EnumLiteral.NUM)
      Console.Write(" (NUM " + node.nval + ")");
    else if(node.type == EnumLiteral.BOOL)
      Console.Write(" (BOOL " + node.nval + ")");
    else if(node.type == EnumLiteral.STR)
      Console.Write(" (STR '" + node.sval + "')");
  }

  public override void DoVisit(AST_BinaryOpExp node)
  {
    Console.Write("(BOP " + node.type);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_UnaryOpExp node)
  {
    Console.Write("(UOP " + node.type);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_New node)
  {
    Console.Write("(NEW " + node.type + " " + node.ntype);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_VarDecl node)
  {
    Console.Write("(VARDECL " + node.name + " " + node.nname);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_JsonObj node)
  {
    Console.Write("(JOBJ " + node.ntype);
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_JsonArr node)
  {
    Console.Write("(JARR ");
    VisitChildren(node);
    Console.Write(")");
  }

  public override void DoVisit(AST_JsonPair node)
  {
    Console.Write("(JPAIR " + node.name + " " + node.nname);
    VisitChildren(node);
    Console.Write(")");
  }
}

public class FastStackDynamic<T> : List<T>
{
  public FastStackDynamic(int startingCapacity)
    : base(startingCapacity)
  {}

  public T Push(T item)
  {
    this.Add(item);
    return item;
  }

  public void Expand(int size)
  {
    for(int i = 0; i < size; i++)
      this.Add(default(T));
  }

  public void Zero(int index)
  {
    this[index] = default(T);
  }

  public T Peek()
  {
    return this[this.Count - 1];
  }

  public void CropAtCount(int p)
  {
    RemoveLast(Count - p);
  }

  public void RemoveLast( int cnt = 1)
  {
    if (cnt == 1)
    {
      this.RemoveAt(this.Count - 1);
    }
    else
    {
      this.RemoveRange(this.Count - cnt, cnt);
    }
  }

  public T Pop()
  {
    T retval = this[this.Count - 1];
    this.RemoveAt(this.Count - 1);
    return retval;
  }
}

public class FastStack<T>
{
  T[] storage;
  int head_idx = 0;

  public FastStack(int max_capacity)
  {
    storage = new T[max_capacity];
  }

  public T this[int index]
  {
    get { return storage[index]; }
    set { storage[index] = value; }
  }

  public bool TryGetAt(int index, out T v)
  {
    if(index < 0 || index >= storage.Length)
    {
      v = default(T);
      return false;
    }
    
    v = storage[index];
    return true;
  }

  public T Push(T item)
  {
    if((head_idx+1) < 0 || (head_idx+1) >= storage.Length)
      throw new IndexOutOfRangeException("Out of bounds index: " + head_idx + " (" + storage.Length + ")");
    storage[head_idx++] = item;
    return item;
  }

  public T Peek()
  {
    return storage[head_idx - 1];
  }

  public void SetRelative(int offset, T item)
  {
    storage[head_idx - 1 - offset] = item;
  }

  public void RemoveAtFast(int idx)
  {
    if(idx == (head_idx-1))
    {
      DecFast();
    }
    else
    {
      --head_idx;
      Array.Copy(storage, idx+1, storage, idx, storage.Length-idx-1);
    }
  }

  public void MoveTo(int src, int dst)
  {
    if(src == dst)
      return;

    var tmp = storage[src];

    if(src > dst)
      Array.Copy(storage, dst, storage, dst + 1, src - dst);
    else
      Array.Copy(storage, src + 1, storage, src, dst - src);

    storage[dst] = tmp;
  }

  public T Pop()
  {
    --head_idx;
    T retval = storage[head_idx];
    storage[head_idx] = default(T);
    return retval;
  }

  public T PopFast()
  {
    --head_idx;
    return storage[head_idx];
  }

  public void DecFast()
  {
    --head_idx;
  }

  public void Clear()
  {
    Array.Clear(storage, 0, storage.Length);
    head_idx = 0;
  }

  public int Count
  {
    get { return head_idx; }
  }
}

//NOTE: cache for less GC operations
public static class TempBuffer
{
  static byte[][] tmp_bufs = new byte[][] { new byte[512], new byte[512], new byte[512] };
  static int tmp_buf_counter = 0;
  static public int stats_max_buf = 0;

  static public byte[] Get()
  {
    var buf = tmp_bufs[tmp_buf_counter % 3];
    ++tmp_buf_counter;
    return buf;
  }

  static public void Update(byte[] buf)
  {
    var idx = (tmp_buf_counter-1) % 3;
    //for debug
    //var curr = tmp_bufs[idx];
    //if(curr != buf)
    //  Util.Debug(curr.Length + " VS " + buf.Length);
    tmp_bufs[idx] = buf;
    stats_max_buf = stats_max_buf < buf.Length ? buf.Length : stats_max_buf;
  }
}

// A dictionary that remembers the order that keys were first inserted. If a new entry overwrites an existing entry, 
// the original insertion position is left unchanged. 
// Deleting an entry and reinserting it will move it to the end.
public class OrderedDictionary<TKey, TValue>
{
  // An unordered dictionary of key pairs.
  Dictionary<TKey, TValue> dict;

  // The keys of the dictionary in the exposed order.
  List<TKey> keys = new List<TKey>();

  public OrderedDictionary()
  {
    dict = new Dictionary<TKey, TValue>();
  }

  public OrderedDictionary(IEqualityComparer<TKey> cmp)
  {
    dict = new Dictionary<TKey, TValue>(cmp);
  }

  public int Count
  {
    get {
      return keys.Count;
    }
  }

  public ICollection<TKey> Keys
  {
    get {
      return keys.AsReadOnly();
    }
  }

  // The value at the given index.
  public TValue this[int index]
  {
    get {
      var key = keys[index];
      return dict[key];
    }
    set {
      var key = keys[index];
      dict[key] = value;
    }
  }

  public int IndexOf(TKey key)
  {
    return keys.IndexOf(key);
  }

  public void RemoveAt(int index)
  {
    var key = keys[index];
    dict.Remove(key);
    keys.RemoveAt(index);
  }

  public bool ContainsKey(TKey key)
  {
    return dict.ContainsKey(key);
  }

  public bool TryGetValue(TKey key, out TValue value)
  {
    return dict.TryGetValue(key, out value);
  }

  public void Insert(int index, TKey key, TValue value)
  {
    // Dictionary operation first, so exception thrown if key already exists.
    dict.Add(key, value);
    keys.Insert(index, key);
  }

  public void Add(TKey key, TValue value)
  {
    // Dictionary operation first, so exception thrown if key already exists.
    dict.Add(key, value);
    keys.Add(key);
  }

  public void Remove(TKey key)
  {
    dict.Remove(key);
    keys.Remove(key);
  }

  public void Clear()
  {
    dict.Clear();
    keys.Clear();
  }
}

} //namespace bhl
