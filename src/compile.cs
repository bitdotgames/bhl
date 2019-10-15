using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;

namespace bhl {

public enum Opcodes
{
  Constant  = 1,
  Add       = 2,
  Sub       = 3,
  Div       = 4,
  Mul       = 5,
  SetVar    = 6,
  GetVar    = 7,
  FuncCall  = 8,
  ReturnVal = 9,
  Return    = 10,
  Equal     = 11,
  NotEqual  = 12,
  Greather  = 13,
  Jump      = 14,
  CondJump  = 15
}

public enum SymbolScope
{
  Global = 1
}

public struct SymbolView
{
  public string name;
  public SymbolScope scope;
  public int index;
}

public class SymbolViewTable
{
  Dictionary<string, SymbolView> store = new Dictionary<string, SymbolView>();

  public SymbolView Define(string name)
  {
    var s = new SymbolView()
            {
              name = name,
              index = store.Count,
              scope  = SymbolScope.Global
            };

    if(!store.ContainsKey(name))
    {
     store[name] = s;
     return s;
    }
    else
    {
     return store[name];
    } 
  }

  public SymbolView Resolve(string name)
  {
    SymbolView s;
    if(!store.TryGetValue(name, out s))
     throw new Exception("No such symbol in table " + name);
    return s;
  }
}

public class Compiler : AST_Visitor
{
  class OpDefinition
  {
    public Opcodes name;
    public int[] operand_width; //each array item represents the size of the operand in bytes
  }

  public struct Constant
  {
    public double nval;
    public string sval;
  }

  WriteBuffer bytecode = new WriteBuffer();

  List<WriteBuffer> scopes = new List<WriteBuffer>();

  List<Constant> constants = new List<Constant>();

  SymbolViewTable sv_table = new SymbolViewTable();

  List<SymbolViewTable> symbols = new List<SymbolViewTable>();

  Dictionary<string, uint> func_offset_buffer = new Dictionary<string, uint>();
  
  Dictionary<byte, OpDefinition> opcode_decls = new Dictionary<byte, OpDefinition>();

  WriteBuffer GetCurrentScope()
  {
    if(this.scopes.Count > 0)
     return this.scopes[this.scopes.Count-1];

    return bytecode;
  }

  SymbolViewTable GetCurrentSVTable() //TODO: try to reduce similar Geters
  {
    if(this.symbols.Count > 0)
     return this.symbols[this.symbols.Count-1];

    return sv_table;
  }

  void EnterNewScope()
  {
    scopes.Add(new WriteBuffer());
    symbols.Add(new SymbolViewTable());
  }
 
  long LeaveCurrentScope()
  {
    var index = bytecode.Length;
    var curr_scope = GetCurrentScope();
    var curr_table = GetCurrentSVTable();
    bytecode.Write(curr_scope);
    scopes.Remove(curr_scope);
    symbols.Remove(curr_table);
    return index;
  }

  public Compiler()
  {
    DeclareOpcodes();
  }

  public void Compile(AST ast)
  {
    Visit(ast);
  }

  int AddConstant(double nval)
  {
    for(int i = 0 ; i < constants.Count; ++i)
    {
      if(constants[i].nval == nval)
        return i;
    }
    constants.Add(new Constant() {nval = nval});
    return constants.Count-1;
  }

  int AddConstant(string sval)
  {
    for(int i = 0 ; i < constants.Count; ++i)
    {
      if(constants[i].sval == sval)
        return i;
    }
    constants.Add(new Constant() {sval = sval});
    return constants.Count-1;
  }

  public List<Constant> GetConstants()
  {
    return constants;
  }

  public Dictionary<string, uint> GetFuncBuffer()
  {
    return func_offset_buffer;
  }

  void DeclareOpcodes()
  {
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Constant,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Add
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Sub
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Div
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Mul
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Equal
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.NotEqual
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Greather
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.SetVar,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.GetVar,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.FuncCall,
        operand_width = new int[] { 4, 1 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Return //TODO: impl this type of return
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.ReturnVal
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.Jump,
        operand_width = new int[] { 2 }
      }
    );
    DeclareOpcode(
      new OpDefinition()
      {
        name = Opcodes.CondJump,
        operand_width = new int[] { 2 }
      }
    );
  }

  void DeclareOpcode(OpDefinition def)
  {
    opcode_decls.Add((byte)def.name, def);
  }

  OpDefinition LookupOpcode(Opcodes op)
  {
    OpDefinition def;
    if(!opcode_decls.TryGetValue((byte)op, out def))
       throw new Exception("No such opcode definition: " + op);
    return def;
  }

  //for testing purposes
  public Compiler TestEmit(Opcodes op, int[] operands = null)
  {
    Emit(op, operands);
    return this;
  }

  void Emit(Opcodes op, int[] operands = null)
  {
    var curr_scope = GetCurrentScope();
    Emit(curr_scope, op, operands);
  }

  void Emit(WriteBuffer buf, Opcodes op, int[] operands = null)
  {
    var def = LookupOpcode(op);

    buf.Write((byte)op);

    if(def.operand_width != null && (operands == null || operands.Length != def.operand_width.Length))
      throw new Exception("Invalid number of operands for opcode:" + op + ", expected:" + def.operand_width.Length);

    for(int i = 0; operands != null && i < operands.Length; ++i)
    {
      int width = def.operand_width[i];
      switch(width)
      {
        case 1:
          buf.Write((uint)operands[i]);
        break;
        case 2:
          buf.Write((uint)operands[i]); //TODO: use ushort
        break;
        case 4:
          buf.Write((uint)operands[i]);
        break;
        default:
          throw new Exception("Not supported operand width: " + width + " for opcode:" + op);
      }
    }
  }

  int EmitConditionStatement(AST_Block ast,int index)
  {
    Visit(ast.children[0]);
    Emit(Opcodes.CondJump, new int[] { index });
    var pointer = GetCurrentScope().Position;
    Visit(ast.children[1]);
    return pointer;
  }

  void InsertIndex(int pointer)
  {
    var curr_scope = GetCurrentScope();
    var scope = curr_scope.GetBytes();
    scope[pointer - 1] = (byte)(curr_scope.Position - pointer);
    curr_scope.Reset(scope,0);
    curr_scope.Write(scope, scope.Length);
  }

  public byte[] GetBytes()
  {
    return bytecode.GetBytes();
  }

#region Visits

  public override void DoVisit(AST_Interim ast)
  {
    VisitChildren(ast);
  }

  public override void DoVisit(AST_Module ast)
  {
    VisitChildren(ast);
  }

  public override void DoVisit(AST_Import ast)
  {
  }

  public override void DoVisit(AST_FuncDecl ast)
  {
    EnterNewScope();
    VisitChildren(ast);
    Emit(Opcodes.ReturnVal);

    func_offset_buffer.Add(ast.name, (uint)LeaveCurrentScope());
  }

  public override void DoVisit(AST_LambdaDecl ast)
  {
  }

  public override void DoVisit(AST_ClassDecl ast)
  {
  }

  public override void DoVisit(AST_EnumDecl ast)
  {
  }

  public override void DoVisit(AST_Block ast)
  {
    int index = 0; //blank placeholder
    switch(ast.type)
    {
      case EnumBlock.IF:
        var pointer = 0;
        switch(ast.children.Count)
        {
          case 2:
            pointer = EmitConditionStatement(ast, index);
            InsertIndex(pointer);
          break;
          case 3:
            pointer = EmitConditionStatement(ast, index);
            Emit(Opcodes.Jump, new int[] { index });
            InsertIndex(pointer);
            pointer = GetCurrentScope().Position;
            Visit(ast.children[2]);
            InsertIndex(pointer);
          break;
          default:
            throw new Exception("Not supported conditions count: " + ast.children.Count);
        }
      break;
      default:
        VisitChildren(ast);
      break;
    }
  }

  public override void DoVisit(AST_TypeCast ast)
  {
  }

  public override void DoVisit(AST_New ast)
  {
  }

  public override void DoVisit(AST_Inc ast)
  {
  }

  public override void DoVisit(AST_Call ast)
  {  
    SymbolView s;
    switch(ast.type)
    {
      case EnumCall.VARW:
        s = GetCurrentSVTable().Define(ast.name);
        Emit(Opcodes.SetVar, new int[] { s.index });
      break;
      case EnumCall.VAR:
        s = GetCurrentSVTable().Resolve(ast.name);
        Emit(Opcodes.GetVar, new int[] { s.index });
      break;
      case EnumCall.FUNC:
        uint offset;
        func_offset_buffer.TryGetValue(ast.name, out offset);
        VisitChildren(ast);
        Emit(Opcodes.FuncCall, new int[] {(int)offset, (int)ast.cargs_bits});
      break;
    }
  }

  public override void DoVisit(AST_Return node)
  {
    VisitChildren(node);
    Emit(Opcodes.ReturnVal);
  }

  public override void DoVisit(AST_Break node)
  {
  }

  public override void DoVisit(AST_PopValue node)
  {
  }

  public override void DoVisit(AST_Literal node)
  {
    Emit(Opcodes.Constant, new int[] { AddConstant(node.nval) });
  }

  public override void DoVisit(AST_BinaryOpExp node)
  {
    VisitChildren(node);

    switch(node.type)
    {
      case EnumBinaryOp.ADD:
        Emit(Opcodes.Add);
      break;
      case EnumBinaryOp.SUB:
        Emit(Opcodes.Sub);
      break;
      case EnumBinaryOp.DIV:
        Emit(Opcodes.Div);
      break;
      case EnumBinaryOp.MUL:
        Emit(Opcodes.Mul);
      break;
      case EnumBinaryOp.EQ:
        Emit(Opcodes.Equal);
      break;
      case EnumBinaryOp.NQ:
        Emit(Opcodes.NotEqual);
      break;
      case EnumBinaryOp.GT:
        Emit(Opcodes.Greather);
      break;
      default:
        throw new Exception("Not supported type: " + node.type);
    }
  }

  public override void DoVisit(AST_UnaryOpExp node)
  {
  }

  public override void DoVisit(AST_VarDecl node)
  {
    SymbolView s = GetCurrentSVTable().Define(node.name);
    Emit(Opcodes.SetVar, new int[] { s.index });
  }

  public override void DoVisit(bhl.AST_JsonObj node)
  {
  }

  public override void DoVisit(bhl.AST_JsonArr node)
  {
  }

  public override void DoVisit(bhl.AST_JsonPair node)
  {
  }

#endregion

}

public class WriteBuffer
{
  MemoryStream stream = new MemoryStream();
  public ushort Position { get { return (ushort)stream.Position; } }
  public long Length { get { return stream.Length; } }

  //const int MaxStringLength = 1024 * 32;
  //static Encoding string_encoding = new UTF8Encoding();
  //static byte[] string_write_buffer = new byte[MaxStringLength];

  public WriteBuffer() {}

  public WriteBuffer(byte[] buffer)
  {
    //NOTE: new MemoryStream(buffer) would make it non-resizable so we write it manually
    stream.Write(buffer, 0, buffer.Length);
  }

  public void Reset(byte[] buffer, int size)
  {
    stream.SetLength(0);
    stream.Write(buffer, 0, size);
    stream.Position = 0;
  }

  public byte[] GetBytes()
  {
    //NOTE: documentation: "omits unused bytes"
    return stream.ToArray();
  }

  public void Write(byte value)
  {
    stream.WriteByte(value);
  }

  public void Write(sbyte value)
  {
    stream.WriteByte((byte)value);
  }

  // http://sqlite.org/src4/doc/trunk/www/varint.wiki
  public void Write(uint value)
  {
    if(value <= 240)
    {
      Write((byte)value);
      return;
    }
    if(value <= 2287)
    {
      Write((byte)((value - 240) / 256 + 241));
      Write((byte)((value - 240) % 256));
      return;
    }
    if(value <= 67823)
    {
      Write((byte)249);
      Write((byte)((value - 2288) / 256));
      Write((byte)((value - 2288) % 256));
      return;
    }
    if(value <= 16777215)
    {
      Write((byte)250);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      return;
    }

    // all other values of uint
    Write((byte)251);
    Write((byte)(value & 0xFF));
    Write((byte)((value >> 8) & 0xFF));
    Write((byte)((value >> 16) & 0xFF));
    Write((byte)((value >> 24) & 0xFF));
  }

  public void Write(ulong value)
  {
    if(value <= 240)
    {
      Write((byte)value);
      return;
    }
    if(value <= 2287)
    {
      Write((byte)((value - 240) / 256 + 241));
      Write((byte)((value - 240) % 256));
      return;
    }
    if(value <= 67823)
    {
      Write((byte)249);
      Write((byte)((value - 2288) / 256));
      Write((byte)((value - 2288) % 256));
      return;
    }
    if(value <= 16777215)
    {
      Write((byte)250);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      return;
    }
    if(value <= 4294967295)
    {
      Write((byte)251);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      Write((byte)((value >> 24) & 0xFF));
      return;
    }
    if(value <= 1099511627775)
    {
      Write((byte)252);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      Write((byte)((value >> 24) & 0xFF));
      Write((byte)((value >> 32) & 0xFF));
      return;
    }
    if(value <= 281474976710655)
    {
      Write((byte)253);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      Write((byte)((value >> 24) & 0xFF));
      Write((byte)((value >> 32) & 0xFF));
      Write((byte)((value >> 40) & 0xFF));
      return;
    }
    if(value <= 72057594037927935)
    {
      Write((byte)254);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      Write((byte)((value >> 24) & 0xFF));
      Write((byte)((value >> 32) & 0xFF));
      Write((byte)((value >> 40) & 0xFF));
      Write((byte)((value >> 48) & 0xFF));
      return;
    }

    // all others
    {
      Write((byte)255);
      Write((byte)(value & 0xFF));
      Write((byte)((value >> 8) & 0xFF));
      Write((byte)((value >> 16) & 0xFF));
      Write((byte)((value >> 24) & 0xFF));
      Write((byte)((value >> 32) & 0xFF));
      Write((byte)((value >> 40) & 0xFF));
      Write((byte)((value >> 48) & 0xFF));
      Write((byte)((value >> 56) & 0xFF));
    }
  }

  public void Write(char value)
  {
    Write((byte)value);
  }

  public void Write(short value)
  {
    Write((byte)(value & 0xff));
    Write((byte)((value >> 8) & 0xff));
  }

  public void Write(ushort value)
  {
    Write((byte)(value & 0xff));
    Write((byte)((value >> 8) & 0xff));
  }

  //NOTE: do we really need non-packed versions?
  //public void Write(int value)
  //{
  //  // little endian...
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //}

  //public void Write(uint value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //}

  //public void Write(long value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //  Write((byte)((value >> 32) & 0xff));
  //  Write((byte)((value >> 40) & 0xff));
  //  Write((byte)((value >> 48) & 0xff));
  //  Write((byte)((value >> 56) & 0xff));
  //}

  //public void Write(ulong value)
  //{
  //  Write((byte)(value & 0xff));
  //  Write((byte)((value >> 8) & 0xff));
  //  Write((byte)((value >> 16) & 0xff));
  //  Write((byte)((value >> 24) & 0xff));
  //  Write((byte)((value >> 32) & 0xff));
  //  Write((byte)((value >> 40) & 0xff));
  //  Write((byte)((value >> 48) & 0xff));
  //  Write((byte)((value >> 56) & 0xff));
  //}

  public void Write(float value)
  {
    byte[] bytes = BitConverter.GetBytes(value);
    Write(bytes, bytes.Length);
  }

  public void Write(double value)
  {
    byte[] bytes = BitConverter.GetBytes(value);
    Write(bytes, bytes.Length);
  }

  //TODO:
  //public void Write(string value)
  //{
  //  if(value == null)
  //  {
  //    Write((ushort)0);
  //    return;
  //  }

  //  int len = string_encoding.GetByteCount(value);

  //  if(len >= MaxStringLength)
  //    throw new IndexOutOfRangeException("Serialize(string) too long: " + value.Length);

  //  Write((ushort)len);
  //  int numBytes = string_encoding.GetBytes(value, 0, value.Length, string_write_buffer, 0);
  //  stream.Write(string_write_buffer, 0, numBytes);
  //}

  public void Write(bool value)
  {
    stream.WriteByte((byte)(value ? 1 : 0));
  }

  public void Write(WriteBuffer buffer)
  {
    buffer.WriteTo(stream);
  }

  void WriteTo(MemoryStream buffer_stream)
  {
    stream.WriteTo(buffer_stream);
  }

  public void Write(byte[] buffer, int count)
  {
    stream.Write(buffer, 0, count);
  }

  public void Write(byte[] buffer, int offset, int count)
  {
    stream.Write(buffer, offset, count);
  }

  public void SeekZero()
  {
    stream.Seek(0, SeekOrigin.Begin);
  }

  public void StartMessage(short type)
  {
    SeekZero();

    // two bytes for size, will be filled out in FinishMessage
    Write((ushort)0);

    // two bytes for message type
    Write(type);
  }

  public void FinishMessage()
  {
    // jump to zero, replace size (short) in header, jump back
    long oldPosition = stream.Position;
    ushort sz = (ushort)(Position - (sizeof(ushort) * 2)); // length - header(short,short)

    SeekZero();
    Write(sz);
    stream.Position = oldPosition;
  }
}

} //namespace bhl