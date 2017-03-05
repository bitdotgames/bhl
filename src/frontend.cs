using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Antlr4.Runtime.Dfa;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Sharpen;

namespace bhl {

public class ParseError : Exception
{
  public ParseError(string str)
    : base(str)
  {}
}

public class ErrorLexerListener : IAntlrErrorListener<int>
{
  public virtual void SyntaxError(IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
  {
    throw new ParseError("@(" + line + "," + charPositionInLine + ") " + msg);
  }
}

public class ErrorStrategy : DefaultErrorStrategy
{
  public override void Sync(Parser recognizer) {}
}

public class ErrorParserListener : IParserErrorListener
{
  public virtual void SyntaxError(IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
  {
    throw new ParseError("@(" + line + "," + charPositionInLine + ") " + msg);
  }

  public virtual void ReportAmbiguity(Parser recognizer, DFA dfa, int startIndex, int stopIndex, bool exact, BitSet ambigAlts, ATNConfigSet configs)
  {}
  public virtual void ReportAttemptingFullContext(Parser recognizer, DFA dfa, int startIndex, int stopIndex, BitSet conflictingAlts, SimulatorState conflictState)
  {}
  public virtual void ReportContextSensitivity(Parser recognizer, DFA dfa, int startIndex, int stopIndex, int prediction, SimulatorState acceptState)
  {}
}

public class AST_Builder : bhlBaseVisitor<AST>
{
  static int lambda_id = 0;

  static int NextLambdaId()
  {
    Interlocked.Increment(ref lambda_id);
    return lambda_id;
  }

  Module curr_m;
  ModuleRegistry mreg;
  bool defs_only;
  ITokenStream tokens;
  ParseTreeProperty<WrappedNode> nodes = new ParseTreeProperty<WrappedNode>();
  LocalScope mscope;
  GlobalScope globals;
  Scope curr_scope;

  public static CommonTokenStream Source2Tokens(Stream s)
  {
    var ais = new AntlrInputStream(s);
    var lex = new bhlLexer(ais);
    lex.AddErrorListener(new ErrorLexerListener());
    return new CommonTokenStream(lex);
  }

  public static AST_Module File2AST(string file, GlobalScope globs, ModuleRegistry mr)
  {
    using(var sfs = File.OpenRead(file))
    {
      var mod = new Module(mr.FilePath2ModulePath(file), file);
      return Source2AST(mod, sfs, globs, mr);
    }
  }
  
  public static AST_Module Source2AST(Module module, Stream src, GlobalScope globs, ModuleRegistry mr, bool defs_only = false)
  {
    try
    {
      var tokens = Source2Tokens(src);
      var p = new bhlParser(tokens);
      p.AddErrorListener(new ErrorParserListener());
      p.ErrorHandler = new ErrorStrategy();

      var cst = p.program();

      var b = new AST_Builder(module, tokens, globs, mr, defs_only);
      var ast = b.VisitProgram(cst) as AST_Module;
      if(ast == null)
        throw new Exception("Bad AST");
      return ast;
    }
    catch(ParseError e)
    {
      throw new UserError(module.file_path, e.Message);
    }
  }

  public static bhlParser.RetTypeContext ParseType(string type)
  {
    try
    {
      var tokens = Source2Tokens(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(type)));
      var p = new bhlParser(tokens);
      p.AddErrorListener(new ErrorParserListener());
      p.ErrorHandler = new ErrorStrategy();
      return p.retType();
    }
    catch(Exception)
    {
      return null;
    }
  }

  static public void Source2Bin(Module module, Stream src, Stream dst, GlobalScope globs, ModuleRegistry mr)
  {
    var ast = Source2AST(module, src, globs, mr);
    Util.Meta2Bin(ast, dst);
  }

  public void FireError(string msg) 
  {
    //Console.Error.WriteLine(err);
    throw new UserError(curr_m.file_path, msg);
  }

  public AST_Builder(Module module, ITokenStream tokens, GlobalScope globs, ModuleRegistry mreg, bool defs_only = false)
  {
    this.curr_m = module;

    this.tokens = tokens;
    this.defs_only = defs_only;
    this.mscope = new LocalScope(globs);
    this.globals = globs;
    if(this.globals == null)
      throw new Exception("Global scope is not set");
    this.mreg = mreg;

    curr_scope = this.mscope;
  }

  public string Location(IParseTree t)
  {
    return Wrap(t).Location();
  }

  public string LocationAfter(IParseTree t)
  {
    return Wrap(t).LocationAfter();
  }

  public WrappedNode Wrap(IParseTree t)
  {
    var n = nodes.Get(t);
    if(n == null)
    {
      n = new WrappedNode();
      n.tree = t;
      n.tokens = tokens;
      n.builder = this;
      nodes.Put(t, n);
    }
    return n;
  }

  public override AST VisitProgram(bhlParser.ProgramContext ctx)
  {
    AST_Module ast = AST_Util.New_Module(curr_m.GetId());
    for(int i=0;i<ctx.progblock().Length;++i)
      AddToModule(ast, ctx.progblock()[i]);
    return ast;
  }

  public void AddToModule(AST_Module node, bhlParser.ProgblockContext ctx)
  {
    try
    {
      var imps = ctx.imports();
      if(imps != null)
        node.AddChild(Visit(imps));

      var decls = ctx.funcDecls(); 
      AddToModule(node, decls);
    }
    catch(UserError e)
    {
      //NOTE: if file is not set we need to update it and re-throw the exception
      if(e.file == null)
        e.file = curr_m.file_path;
      throw e;
    }
  }

  public override AST VisitImports(bhlParser.ImportsContext ctx)
  {
    var res = AST_Util.New_Imports();

    if(!defs_only)
    {
      var imps = ctx.mimport();
      for(int i=0;i<imps.Length;++i)
        AddImport(res, imps[i]);
    }

    return res;
  }

  public void AddImport(AST_Import node, bhlParser.MimportContext ctx)
  {
    var import = ctx.NORMALSTRING().GetText();
    //removing quotes
    import = import.Substring(1, import.Length-2);
    
    var module = mreg.ImportModule(curr_m, globals, import);
    //NOTE: nulls means module is already imported
    if(module != null)
    {
      mscope.Append(module.symbols);
      node.modules.Add(module.GetId());
    }
  }

  public override AST VisitSymbCall(bhlParser.SymbCallContext ctx)
  {
    var exp = ctx.callExp(); 
    var res = Visit(exp);
    var eval_type = Wrap(exp).eval_type;
    if(eval_type != null && eval_type != SymbolTable._void)
      FireError(Location(ctx) + " : Non consumed value");
    return res;
  }

  public override AST VisitCallExp(bhlParser.CallExpContext ctx)
  {
    Type curr_type = null;

    var node = ProcChainedCall(ctx.NAME(), ctx.chainExp(), ref curr_type);

    Wrap(ctx).eval_type = curr_type;

    return node;
  }

  void ChainCallNode(AST new_node, ref AST curr_node, ref AST top_node)
  {
    if(curr_node != null)
      curr_node.AddChild(new_node);
    else
      curr_node = new_node;
    curr_node = new_node;

    if(top_node == null)
      top_node = new_node;
  }

  AST ProcChainedCall(ITerminalNode root_name, bhlParser.ChainExpContext[] chain, ref Type curr_type)
  {
    var orig_scope = curr_scope;

    AST top_node = null;

    ITerminalNode curr_name = root_name;
    ClassSymbol curr_class = null;
    AST curr_node = null;

    if(root_name != null)
    {
      var root_str_name = root_name.GetText();
      var name_symb = curr_scope.resolve(root_str_name);
      if(name_symb == null)
        FireError(Location(root_name) + " : Symbol not resolved");
      curr_type = name_symb.type.Get();
    }

    if(chain != null)
    {
      for(int c=0;c<chain.Length;++c)
      {
        var ch = chain[c];

        var cargs = ch.callArgs();
        var ma = ch.memberAccess();
        var arra = ch.arrAccess();

        if(cargs != null)
        {
          var new_node = ProcCallChainItem(curr_name, cargs, null, curr_class, ref curr_type);
          ChainCallNode(new_node, ref curr_node, ref top_node);
          curr_class = null;
          curr_name = null;
        }
        else if(arra != null)
        {
          var new_node = ProcCallChainItem(curr_name, null, arra, curr_class, ref curr_type);
          ChainCallNode(new_node, ref curr_node, ref top_node);
          curr_class = null;
          curr_name = null;
        }
        else if(ma != null)
        {
          if(curr_name != null)
          {
            var new_node = ProcCallChainItem(curr_name, null, null, curr_class, ref curr_type);
            ChainCallNode(new_node, ref curr_node, ref top_node);
          }

          curr_class = curr_type as ClassSymbol; 
          if(curr_class == null)
            FireError(Location(ma) + " : Type '" + curr_type.GetName() + "' doesn't support member access via '.' ");

          curr_name = ma.NAME();
        }
      }
    }

    //checking the leftover of the call chain
    if(curr_name != null)
    {
      var new_node = ProcCallChainItem(curr_name, null, null, curr_class, ref curr_type);
      ChainCallNode(new_node, ref curr_node, ref top_node);
    }

    curr_scope = orig_scope;

    return top_node;
  }

  AST_Call ProcCallChainItem(ITerminalNode name, bhlParser.CallArgsContext cargs, bhlParser.ArrAccessContext arra, ClassSymbol class_scope, ref Type type)
  {
    AST_Call node = null;

    if(name != null)
    {
      string str_name = name.GetText();
      var name_symb = class_scope == null ? curr_scope.resolve(str_name) : class_scope.resolve(str_name);
      if(name_symb == null)
        FireError(Location(name) + " : Symbol not resolved");

      var var_symb = name_symb as VariableSymbol;
      var func_symb = name_symb as FuncSymbol;

      //func or method call
      if(cargs != null)
      {
        if(name_symb is FieldSymbol)
          FireError(Location(name) + " : Symbol is not a function");

        //func ptr
        if(var_symb != null && var_symb.type.Get() is FuncType)
        {
          var ftype = var_symb.type.Get() as FuncType;
          node = AST_Util.New_Call(EnumCall.FUNC_PTR, str_name, Hash.CRC28(str_name));
          AddCallArgs(ftype, cargs, ref node);
          type = ftype.ret_type.Get();
        }
        else if(func_symb != null)
        {
          node = AST_Util.New_Call(class_scope != null ? EnumCall.MFUNC : EnumCall.FUNC, str_name, func_symb.GetCallId(), class_scope);
          AddCallArgs(func_symb, cargs, ref node);
          type = func_symb.GetReturnType();
        }
        else
        {
          //NOTE: let's try fetching func symbol from the module scope
          func_symb = mscope.resolve(str_name) as FuncSymbol;
          if(func_symb != null)
          {
            node = AST_Util.New_Call(EnumCall.FUNC, str_name, func_symb.GetCallId());
            AddCallArgs(func_symb, cargs, ref node);
            type = func_symb.GetReturnType();
          }
          else
          {
            FireError(Location(name) +  " : Symbol is not not a function");
          }
        }
      }
      //variable or attribute call
      else
      {
        if(var_symb != null)
        {
          node = AST_Util.New_Call(class_scope != null ? EnumCall.MVAR : EnumCall.VAR, str_name, Hash.CRC28(str_name), class_scope);
          type = var_symb.type.Get();
        }
        else if(func_symb != null)
        {
          var call_func_symb = mscope.resolve(str_name) as FuncSymbol;
          if(call_func_symb == null)
            FireError(Location(name) +  " : No such function found");
          ulong func_call_id = call_func_symb.GetCallId();

          node = AST_Util.New_Call(EnumCall.FUNC2VAR, str_name, func_call_id);
          type = func_symb.type.Get();
        }
        else
        {
          FireError(Location(name) +  " : Symbol usage is not valid");
        }
      }
    }
    else if(cargs != null)
    {
      var ftype = (FuncType)type;
      if(ftype == null)
        throw new Exception("Func type is missing");
      
      node = AST_Util.New_Call(EnumCall.FUNC_PTR_POP);
      AddCallArgs(ftype, cargs, ref node);
      type = ftype.ret_type.Get();
    }

    if(arra != null)
      node = AddArrIndex(node, arra, ref type);

    if(node == null)
      throw new Exception("Node not assigned");

    return node;
  }

  AST_Call AddArrIndex(AST_Call root, bhlParser.ArrAccessContext arra, ref Type type)
  {
    var arr_type = type as ArrayTypeSymbol;
    if(arr_type == null)
      FireError(Location(arra) +  " : Accessing not an array type '" + type.GetName() + "'");

    var node = AST_Util.New_Call(EnumCall.ARR_IDX);
    node.scope_ntype = arr_type.GetNtype();

    var arr_exp = arra.exp();
    node.AddChild(Visit(arr_exp));

    if(root != null)
      root.AddChild(node);

    if(Wrap(arr_exp).eval_type != SymbolTable._int)
      FireError(Location(arr_exp) +  " : Array index expression is not of type int");

    type = arr_type.original.Get();

    return root == null ? node : root;
  }

  class NormCallArg
  {
    public bhlParser.CallArgContext ca;
    public Symbol orig;
  }

  void AddCallArgs(FuncSymbol func_symb, bhlParser.CallArgsContext cargs, ref AST_Call new_node)
  {     
    var func_args = func_symb.GetArgs();
    var total_args_num = func_symb.GetTotalArgsNum();
    var default_args_num = func_symb.GetDefaultArgsNum();
    int required_args_num = total_args_num - default_args_num;
    int args_passed = 0;

    List<NormCallArg> norm_cargs = new List<NormCallArg>();
    for(int i=0;i<total_args_num;++i)
    {
      var arg = new NormCallArg();
      arg.orig = (Symbol)func_args[i];
      norm_cargs.Add(arg); 
    }

    //1. normalizing call args
    for(int ci=0;ci<cargs.callArg().Length;++ci)
    {
      var ca = cargs.callArg()[ci];
      var ca_name = ca.NAME();

      var idx = ci;
      //NOTE: checking if it's a named arg and finding its index
      if(ca_name != null)
      {
        idx = func_args.FindStringKeyIndex(ca_name.GetText());
        if(idx == -1)
          FireError(Location(ca_name) +  ": No such named argument");
      }
      
      if(idx >= func_args.Count)
        FireError(Location(ca) +  ": There is no argument " + (idx + 1) + ", total arguments " + func_args.Count);

      if(idx >= func_args.Count)
        FireError(Location(ca) +  ": Too many arguments for function");

      norm_cargs[idx].ca = ca;
    }

    IParseTree prev_ca = null;
    //2. traversing normalized args
    for(int i=0;i<norm_cargs.Count;++i)
    {
      var ca = norm_cargs[i].ca;

      if(ca == null)
      {
        var next_arg = FindNextCallArg(cargs, prev_ca);

        if(i < required_args_num)
          FireError(Location(next_arg) +  ": Missing argument '" + norm_cargs[i].orig.name + "'");
        //rest are args by default
        else if(HasAllDefaultArgsAfter(norm_cargs, i))
          break;
        else
        {
          var default_arg = func_symb.GetDefaultArgsExprAt(i);
          if(default_arg != null)
          {
            ++args_passed;
            new_node.AddChild(Visit(default_arg));
          }
          else
            FireError(Location(next_arg) +  ": Missing argument '" + norm_cargs[i].orig.name + "'");
        }
      }
      else
      {
        prev_ca = ca;
        ++args_passed;

        var func_arg_symb = (Symbol)func_args[i];
        var func_arg_type = func_arg_symb.node == null ? func_arg_symb.type.Get() : func_arg_symb.node.eval_type;  

        if(ca.isRef() == null && func_symb.IsArgRefAt(i))
          FireError(Location(ca) +  ": 'ref' is missing");
        else if(ca.isRef() != null && !func_symb.IsArgRefAt(i))
          FireError(Location(ca) +  ": argument is not a 'ref'");

        PushJsonType(func_arg_type);
        new_node.AddChild(Visit(ca));
        PopJsonType();

        var wca = Wrap(ca);

        //NOTE: if symbol is from bindings we don't have a source node attached to it
        if(func_arg_symb.node == null)
          SymbolTable.CheckAssign(func_arg_symb.type.Get(), wca);
        else
          SymbolTable.CheckAssign(func_arg_symb.node, wca);
      }
    }

    new_node.cargs_num = args_passed;
  }

  void AddCallArgs(FuncType func_type, bhlParser.CallArgsContext cargs, ref AST_Call new_node)
  {     
    var func_args = func_type.arg_types;

    int ca_len = cargs.callArg().Length; 
    IParseTree prev_ca = null;
    for(int i=0;i<func_args.Count;++i)
    {
      var arg_type = func_args[i]; 

      if(i == ca_len)
      {
        var next_arg = FindNextCallArg(cargs, prev_ca);
        FireError(Location(next_arg) +  ": Missing argument of type '" + arg_type.name + "'");
      }

      var ca = cargs.callArg()[i];
      var ca_name = ca.NAME();

      if(ca_name != null)
        FireError(Location(ca_name) +  ": Named arguments not supported for function pointers");

      var type = arg_type.Get();
      PushJsonType(type);
      new_node.AddChild(Visit(ca));
      PopJsonType();

      var wca = Wrap(ca);
      SymbolTable.CheckAssign(type, wca);

      if(arg_type.is_ref && ca.isRef() == null)
        FireError(Location(ca) +  ": 'ref' is missing");
      else if(!arg_type.is_ref && ca.isRef() != null)
        FireError(Location(ca) +  ": argument is not a 'ref'");

      prev_ca = ca;
    }

    if(ca_len != func_args.Count)
      FireError(Location(cargs) +  ": Too many arguments");

    new_node.cargs_num = func_args.Count;
  }

  IParseTree FindNextCallArg(bhlParser.CallArgsContext cargs, IParseTree curr)
  {
    for(int i=0;i<cargs.callArg().Length;++i)
    {
      var ch = cargs.callArg()[i];
      if(ch == curr && (i+1) < cargs.callArg().Length)
        return cargs.callArg()[i+1];
    }

    //NOTE: graceful fallback
    return cargs;
  }

  bool HasAllDefaultArgsAfter(List<NormCallArg> arr, int idx)
  {
    for(int i=idx+1;i<arr.Count;++i)
    {
      if(arr[i].ca != null)
        return false;
    }
    return true;
  }

  public override AST VisitExpLambda(bhlParser.ExpLambdaContext ctx)
  {
    var tr = globals.type(ctx.funcLambda().retType());
    if(tr.type == null)
      FireError(Location(tr.node) + ": Type '" + tr.name + "' not found");

    var func_name = curr_m.GetId() + "_" + NextLambdaId(); 
    var node = AST_Util.New_LambdaDecl(curr_m.GetId(), tr.name, func_name);
    var lambda_node = Wrap(ctx);
    var symb = new LambdaSymbol(globals, node, this.func_decl_stack, lambda_node, func_name, tr, mscope);

    var fdecl = new FuncDecl(node, symb);
    PushFuncDecl(fdecl);

    var useblock = ctx.funcLambda().useBlock();
    if(useblock != null)
    {
      for(int i=0;i<useblock.refName().Length;++i)
      {
        var un = useblock.refName()[i]; 
        var un_name_str = un.NAME().GetText(); 
        var un_symb = curr_scope.resolve(un_name_str);
        if(un_symb == null)
          FireError(Location(un) +  " : Symbol '" + un_name_str + "' not defined in parent scope");

        fdecl.AddUseParam(un_symb, un.isRef() != null);
      }
    }

    var scope_backup = curr_scope;
    curr_scope = symb;

    var fparams = ctx.funcLambda().funcParams();
    if(fparams != null)
      node.fparams().AddChild(Visit(fparams));

    //NOTE: for now defining lambdas in a module scope 
    mscope.define(symb);

    //NOTE: while we are inside lambda the eval type is the return type of
    Wrap(ctx).eval_type = symb.GetReturnType();

    node.block().AddChild(Visit(ctx.funcLambda().funcBlock()));

    PopFuncDecl();

    //NOTE: once we are out of lambda the eval type is the lambda itself
    var curr_type = symb.type.Get(); 
    Wrap(ctx).eval_type = curr_type;

    curr_scope = scope_backup;

    var chain = ctx.funcLambda().chainExp(); 
    if(chain != null)
    {
      var interim = AST_Util.New_Interim();
      interim.AddChild(node);
      interim.AddChild(ProcChainedCall(null, chain, ref curr_type));
      Wrap(ctx).eval_type = curr_type;
      return interim;
    }
    else
      return node;
  }

  public override AST VisitCallArg(bhlParser.CallArgContext ctx)
  {
    var exp = ctx.exp();
    var json = ctx.jsonObject();

    if(exp != null)
    {
      var node = Visit(exp);
      Wrap(ctx).eval_type = Wrap(exp).eval_type;
      return node;
    }
    //NOTE: json object
    else
    {
      var node = Visit(json);
      Wrap(ctx).eval_type = Wrap(json).eval_type;
      return node;
    }
  }

  public override AST VisitJsonObject(bhlParser.JsonObjectContext ctx)
  {
    var curr_type = PeekJsonType();
    if(curr_type == null)
      FireError(Location(ctx) + ": Json context not set");

    if(!(curr_type is ClassSymbol) || (curr_type is ArrayTypeSymbol))
      FireError(Location(ctx) + ": Object is not expected, need '" + curr_type + "'");

    Wrap(ctx).eval_type = curr_type;
    var root_type_name = curr_type.GetName();

    var node = AST_Util.New_JsonObj(root_type_name);

    var pairs = ctx.jsonPair();
    for(int i=0;i<pairs.Length;++i)
    {
      var pair = pairs[i]; 
      node.AddChild(Visit(pair));
    }

    return node;
  }

  public override AST VisitJsonArray(bhlParser.JsonArrayContext ctx)
  {
    var curr_type = PeekJsonType();
    if(curr_type == null)
      FireError(Location(ctx) + ": Json context is not set");

    if(!(curr_type is ArrayTypeSymbol))
      FireError(Location(ctx) + ": Array is not expected, need '" + curr_type + "'");

    var arr_type = curr_type as ArrayTypeSymbol;
    PushJsonType(arr_type.original.Get());

    var node = AST_Util.New_JsonArr(arr_type);

    var vals = ctx.jsonValue();
    for(int i=0;i<vals.Length;++i)
    {
      node.AddChild(Visit(vals[i]));
    }

    PopJsonType();

    return node;
  }

  public override AST VisitJsonPair(bhlParser.JsonPairContext ctx)
  {
    var curr_type = PeekJsonType();
    var scoped_symb = (ClassSymbol)curr_type;
    if(scoped_symb == null)
      FireError(Location(ctx) + ": Expecting class type, got '" + curr_type + "' instead");

    var name_str = ctx.NAME().GetText();
    
    var member = scoped_symb.resolve(name_str);
    if(member == null)
      FireError(Location(ctx) + ": No such attribute '" + name_str + "' in '" + scoped_symb + "'");

    var node = AST_Util.New_JsonPair(curr_type.GetName(), name_str);

    PushJsonType(member.type.Get());

    var jval = ctx.jsonValue(); 
    node.AddChild(Visit(jval));

    PopJsonType();

    Wrap(ctx).eval_type = member.type.Get();

    return node;
  }

  public override AST VisitJsonValue(bhlParser.JsonValueContext ctx)
  {
    var exp = ctx.exp();
    var jobj = ctx.jsonObject();
    var jarr = ctx.jsonArray();

    if(exp != null)
    {
      var curr_type = PeekJsonType();
      var res = Visit(exp);
      Wrap(ctx).eval_type = Wrap(exp).eval_type;

      SymbolTable.CheckAssign(curr_type, Wrap(exp));

      return res;
    }
    else if(jobj != null)
    {
      return Visit(jobj);
    }
    else
    {
      return Visit(jarr);
    }
  }

  public override AST VisitExpStaticCall(bhlParser.ExpStaticCallContext ctx)
  {
    var exp = ctx.staticCallExp(); 
    var ctx_name = exp.NAME();
    var enum_symb = globals.resolve(ctx_name.GetText()) as EnumSymbol;
    if(enum_symb == null)
      FireError(Location(ctx) + ": Type '" + ctx_name + "' not found");

    var item_name = exp.staticCallItem().NAME();
    var enum_val = enum_symb.findValue(Hash.CRC28(item_name.GetText()));

    if(enum_val == null)
      FireError(Location(ctx) + ": Enum value not found '" + item_name.GetText() + "'");

    Wrap(ctx).eval_type = enum_symb;

    var node = AST_Util.New_Literal(EnumLiteral.NUM);
    node.nval = enum_val.val;
    return node;
  }

  public override AST VisitExpCall(bhlParser.ExpCallContext ctx)
  {
    var exp = ctx.callExp(); 
    var res = Visit(exp);
    Wrap(ctx).eval_type = Wrap(exp).eval_type;
    return res;
  }

  public override AST VisitExpNew(bhlParser.ExpNewContext ctx)
  {
    var tr = globals.type(ctx.type());
    if(tr.type == null)
      FireError(Location(tr.node) + ": Type '" + tr.node + "' not found");

    var res = AST_Util.New_New((ClassSymbol)tr.type);
    Wrap(ctx).eval_type = tr.type;

    return res;
  }

  public override AST VisitAssignExp(bhlParser.AssignExpContext ctx)
  {
    var exp = ctx.exp();
    var res = Visit(exp);
    Wrap(ctx).eval_type = Wrap(exp).eval_type;
    return res;
  }

  public override AST VisitExpTypeCast(bhlParser.ExpTypeCastContext ctx)
  {
    var tr = globals.type(ctx.type());
    if(tr.type == null)
      FireError(Location(tr.node) + ": Type '" + tr.name + "' not found");

    var node = AST_Util.New_TypeCast(tr.name);
    var exp = ctx.exp();
    node.AddChild(Visit(exp));

    Wrap(ctx).eval_type = tr.type;

    SymbolTable.CheckCast(Wrap(ctx), Wrap(exp)); 

    return node;
  }

  public override AST VisitExpUnary(bhlParser.ExpUnaryContext ctx)
  {
    EnumUnaryOp type;
    var op = ctx.operatorUnary().GetText(); 
    if(op == "-")
      type = EnumUnaryOp.NEG;
    else if(op == "!")
      type = EnumUnaryOp.NOT;
    else
      throw new Exception("Unknown type");

    var node = AST_Util.New_UnaryOpExp(type);
    var exp = ctx.exp(); 
    node.AddChild(Visit(exp));

    Wrap(ctx).eval_type = type == EnumUnaryOp.NEG ? 
      SymbolTable.Uminus(Wrap(exp)) : 
      SymbolTable.Unot(Wrap(exp));

    return node;
  }

  public override AST VisitExpParen(bhlParser.ExpParenContext ctx)
  {
    var node = AST_Util.New_Interim();
    var exp = ctx.exp(); 
    node.AddChild(Visit(exp));

    Wrap(ctx).eval_type = Wrap(exp).eval_type;

    return node;
  }

  public override AST VisitExpAddSub(bhlParser.ExpAddSubContext ctx)
  {
    EnumBinaryOp type;
    var op = ctx.operatorAddSub().GetText(); 
    if(op == "+")
      type = EnumBinaryOp.ADD;
    else if(op == "-")
      type = EnumBinaryOp.SUB;
    else
      throw new Exception("Unknown type");

    var node = AST_Util.New_BinaryOpExp(type);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);
    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Bop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpMulDivMod(bhlParser.ExpMulDivModContext ctx)
  {
    var op = ctx.operatorMulDivMod().GetText(); 

    EnumBinaryOp type;
    if(op == "*")
      type = EnumBinaryOp.MUL;
    else if(op == "/")
      type = EnumBinaryOp.DIV;
    else if(op == "%")
      type = EnumBinaryOp.MOD;
    else
      throw new Exception("Unknown type");

    var node = AST_Util.New_BinaryOpExp(type);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);
    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Bop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpCompare(bhlParser.ExpCompareContext ctx)
  {
    var op = ctx.operatorComparison().GetText(); 

    EnumBinaryOp type;
    if(op == ">")
      type = EnumBinaryOp.GT;
    else if(op == ">=")
      type = EnumBinaryOp.GTE;
    else if(op == "<")
      type = EnumBinaryOp.LT;
    else if(op == "<=")
      type = EnumBinaryOp.LTE;
    else if(op == "==")
      type = EnumBinaryOp.EQ;
    else if(op == "!=")
      type = EnumBinaryOp.NQ;
    else
      throw new Exception("Unknown type");

    var node = AST_Util.New_BinaryOpExp(type);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);
    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    if(type == EnumBinaryOp.EQ || type == EnumBinaryOp.NQ)
      Wrap(ctx).eval_type = SymbolTable.Eqop(Wrap(exp_0), Wrap(exp_1));
    else
      Wrap(ctx).eval_type = SymbolTable.Relop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpBitAnd(bhlParser.ExpBitAndContext ctx)
  {
    var node = AST_Util.New_BinaryOpExp(EnumBinaryOp.BIT_AND);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Bitop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpBitOr(bhlParser.ExpBitOrContext ctx)
  {
    var node = AST_Util.New_BinaryOpExp(EnumBinaryOp.BIT_OR);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Bitop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpAnd(bhlParser.ExpAndContext ctx)
  {
    var node = AST_Util.New_BinaryOpExp(EnumBinaryOp.AND);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Lop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpOr(bhlParser.ExpOrContext ctx)
  {
    var node = AST_Util.New_BinaryOpExp(EnumBinaryOp.OR);
    var exp_0 = ctx.exp(0);
    var exp_1 = ctx.exp(1);

    node.AddChild(Visit(exp_0));
    node.AddChild(Visit(exp_1));

    Wrap(ctx).eval_type = SymbolTable.Lop(Wrap(exp_0), Wrap(exp_1));

    return node;
  }

  public override AST VisitExpEval(bhlParser.ExpEvalContext ctx)
  {
    //TODO: disallow return statements in eval blocks
    var res = CommonVisitBlock(EnumBlock.EVAL, ctx.block().statement(), false);

    Wrap(ctx).eval_type = SymbolTable._boolean;

    return res;
  }

  public override AST VisitExpLiteralNum(bhlParser.ExpLiteralNumContext ctx)
  {
    var node = AST_Util.New_Literal(EnumLiteral.NUM);

    var number = ctx.number();
    var int_num = number.INT();
    var flt_num = number.FLOAT();
    var hex_num = number.HEX();

    if(int_num != null)
    {
      Wrap(ctx).eval_type = SymbolTable._int;
      node.nval = int.Parse(int_num.GetText());
    }
    else if(flt_num != null)
    {
      Wrap(ctx).eval_type = SymbolTable._float;
      node.nval = float.Parse(flt_num.GetText(), System.Globalization.CultureInfo.InvariantCulture);
    }
    else if(hex_num != null)
    {
      Wrap(ctx).eval_type = SymbolTable._int;
      node.nval = Convert.ToUInt32(hex_num.GetText(), 16);
    }

    return node;
  }

  public override AST VisitExpLiteralFalse(bhlParser.ExpLiteralFalseContext ctx)
  {
    Wrap(ctx).eval_type = SymbolTable._boolean;

    var node = AST_Util.New_Literal(EnumLiteral.BOOL);
    node.nval = 0;
    return node;
  }

  public override AST VisitExpLiteralNull(bhlParser.ExpLiteralNullContext ctx)
  {
    Wrap(ctx).eval_type = SymbolTable._null;

    var node = AST_Util.New_Literal(EnumLiteral.NIL);
    return node;
  }

  public override AST VisitExpLiteralTrue(bhlParser.ExpLiteralTrueContext ctx)
  {
    Wrap(ctx).eval_type = SymbolTable._boolean;

    var node = AST_Util.New_Literal(EnumLiteral.BOOL);
    node.nval = 1;
    return node;
  }

  public override AST VisitExpLiteralStr(bhlParser.ExpLiteralStrContext ctx)
  {
    Wrap(ctx).eval_type = SymbolTable._string;

    var node = AST_Util.New_Literal(EnumLiteral.STR);
    node.sval = ctx.@string().NORMALSTRING().GetText();
    //removing quotes
    node.sval = node.sval.Substring(1, node.sval.Length-2);
    return node;
  }

  //a list since it's easier to traverse by index
  public List<FuncDecl> func_decl_stack = new List<FuncDecl>();

  void PushFuncDecl(FuncDecl decl)
  {
    func_decl_stack.Add(decl);
  }

  void PopFuncDecl()
  {
    func_decl_stack.RemoveAt(func_decl_stack.Count-1);
  }

  FuncDecl PeekFuncDecl()
  {
    if(func_decl_stack.Count == 0)
      return null;

    return func_decl_stack[func_decl_stack.Count-1];
  }

  Stack<Type> json_type_stack = new Stack<Type>();

  void PushJsonType(Type type)
  {
    json_type_stack.Push(type);
  }

  void PopJsonType()
  {
    json_type_stack.Pop();
  }

  Type PeekJsonType()
  {
    if(json_type_stack.Count == 0)
      return null;

    return json_type_stack.Peek();
  }

  int while_stack = 0;

  public override AST VisitReturn(bhlParser.ReturnContext ctx)
  {
    var func_symb = PeekFuncDecl().symbol;
    func_symb.return_statement_found = true;

    var ret_node = AST_Util.New_Return();

    if(func_symb == null)
      FireError(Location(ctx) + ": return statement is not in function");

    var explist = ctx.explist();
    if(explist != null)
    {
      int len = explist.exp().Length;

      if(len == 1)
      {
        var exp = explist.exp()[0];
        var exp_node = Visit(exp);

        //NOTE: workaround for cases like: `return trace(...)`
        //      where exp has void type, in this case
        //      we simply ignore exp_node since return will take
        //      effect right before it
        if(Wrap(exp).eval_type != SymbolTable._void)
        {
          ret_node.AddChild(exp_node);
          SymbolTable.CheckAssign(func_symb.node, Wrap(exp));
          Wrap(ctx).eval_type = Wrap(exp).eval_type;
        }
      }
      else
      {
        var fmret_type = func_symb.GetReturnType() as MultiType;
        if(fmret_type == null)
          FireError(Location(ctx) + ": function doesn't support multi return");

        if(fmret_type.items.Count != len)
          FireError(Location(ctx) + ": multi return size doesn't match destination");

        var ret_type = new MultiType();

        for(int i=0;i<len;++i)
        {
          var exp = explist.exp()[i];
          var exp_node = Visit(exp);
          ret_node.AddChild(exp_node);
          ret_type.items.Add(new TypeRef(Wrap(exp).eval_type));
          
          SymbolTable.CheckAssign(fmret_type.items[i].Get(), Wrap(exp));
        }
        ret_type.Update();
        Wrap(ctx).eval_type = ret_type;
      }
    }
    else
      Wrap(ctx).eval_type = SymbolTable._void;

    return ret_node;
  }

  public override AST VisitBreak(bhlParser.BreakContext ctx)
  {
    if(while_stack == 0)
      FireError(Location(ctx) + ": Not within loop construct");

    var node = AST_Util.New_Break();
    return node;
  }

  //NOTE: not supported yet
  //public override AST VisitContinue(bhlParser.ContinueContext ctx)
  //{
  //  if(while_stack == 0)
  //    FireError(Location(ctx) + ": Not within loop construct");

  //  var node = AST_Util.New_Continue();
  //  return node;
  //}

  public void AddToModule(AST_Module node, bhlParser.FuncDeclsContext ctx)
  {
    var decls = ctx.funcDecl();
    for(int i=0;i<decls.Length;++i)
      node.AddChild(Visit(decls[i]));
  }

  public override AST VisitFuncDecl(bhlParser.FuncDeclContext ctx)
  {
    var tr = globals.type(ctx.retType());
    if(tr.type == null)
      FireError(Location(tr.node) + ": Type '" + tr.name + "' not found");

    var str_name = ctx.NAME().GetText();

    var func_node = Wrap(ctx);
    func_node.eval_type = tr.type;
    var node = AST_Util.New_FuncDecl(curr_m.GetId(), tr.name, str_name);
    var symb = new FuncSymbolAST(globals, node, func_node, str_name, tr, curr_scope, ctx.funcParams());
    mscope.define(symb);
    curr_m.symbols.define(symb);
    curr_scope = symb;

    PushFuncDecl(new FuncDecl(node, symb));

    var fparams = ctx.funcParams();
    if(fparams != null)
      node.fparams().AddChild(Visit(fparams));

    if(!defs_only)
    {
      node.block().AddChild(Visit(ctx.funcBlock()));

      if(tr.type != SymbolTable._void && !symb.return_statement_found)
        FireError(Location(ctx.NAME()) + ": matching 'return' statement not found");
    }

    PopFuncDecl();

    curr_scope = curr_scope.GetEnclosingScope();

    return node;
  }

  public override AST VisitFuncParams(bhlParser.FuncParamsContext ctx)
  {
    var node = AST_Util.New_Interim();

    var func = curr_scope as FuncSymbol;

    var fparams = ctx.funcParamDeclare();
    bool found_default_arg = false;
    for(int i=0;i<fparams.Length;++i)
    {
      var fp = fparams[i]; 
      if(fp.assignExp() != null)
        found_default_arg = true;
      else if(found_default_arg)
        FireError(Location(fp.NAME()) + ": missing default argument expression");

      node.AddChild(Visit(fp));

      func.DefineArg(fp.NAME().GetText());
    }

    return node;
  }

  public override AST VisitVarsDeclare(bhlParser.VarsDeclareContext ctx)
  {
    AST exp_node = null;
    var assign_exp = ctx.assignExp();
    if(assign_exp != null)
      exp_node = Visit(assign_exp);

    var vdecls = ctx.varDeclare();

    //NOTE: multiple decl.vars are chained(next one is added as a child to the prev.one), 
    //      this is kinda hacky but OK for now
    AST root_node = null;
    AST last_node = null;
    for(int i=0;i<vdecls.Length;++i)
    {
      var vd = vdecls[i];

      var node = CommonDeclVar(vd.NAME(), vd.type(), false/*is ref*/, false/*not func arg*/);
      
      if(root_node == null)
        root_node = node;
      
      if(last_node != null)
        last_node.AddChild(node);

      last_node = node;
    }

    if(exp_node != null)
    {
      if(vdecls.Length > 1)
      {
        var exp_type = Wrap(assign_exp).eval_type as MultiType; 
        if(exp_type == null)
          FireError(Location(assign_exp) + ": multi return expected");

        if(exp_type.items.Count != vdecls.Length)
          FireError(Location(assign_exp) + ": multi return size doesn't match destination");

        for(int i=0;i<vdecls.Length;++i)
        {
          var vd = vdecls[i];
          SymbolTable.CheckAssign(Wrap(vd.NAME()), exp_type.items[i].Get());
        }

      }
      else
        SymbolTable.CheckAssign(Wrap(vdecls[0].NAME()), Wrap(assign_exp));
      last_node.AddChild(exp_node);
    }

    return root_node;
  }

  public override AST VisitFuncParamDeclare(bhlParser.FuncParamDeclareContext ctx)
  {
    var name = ctx.NAME();
    var assign_exp = ctx.assignExp();
    bool is_ref = ctx.isRef() != null;

    if(is_ref && assign_exp != null)
      FireError(Location(name) +  ": ref is not allowed to have a default value");

    AST exp_node = null;
    if(assign_exp != null)
      exp_node = Visit(assign_exp);

    var node = CommonDeclVar(name, ctx.type(), is_ref, true/*func arg*/);

    if(exp_node != null)
    {
      SymbolTable.CheckAssign(Wrap(name), Wrap(assign_exp));
      node.AddChild(exp_node);
    }
  
    return node;
  }

  AST CommonDeclVar(ITerminalNode name, bhlParser.TypeContext type, bool is_ref, bool func_arg)
  {
    var str_name = name.GetText();

    var tr = globals.type(type);
    if(tr.type == null)
      FireError(Location(tr.node) +  ": Type '" + tr.name + "' not found");

    var var_node = Wrap(name); 
    var_node.eval_type = tr.type;

    if(is_ref && !func_arg)
      FireError(Location(name) +  ": ref is only allowed in function declaration");

    Symbol symb = func_arg ? 
      (Symbol) new FuncArgSymbol(str_name, tr, is_ref) :
      (Symbol) new VariableSymbol(var_node, str_name, tr);

    var node = AST_Util.New_VarDecl(tr.name, str_name, is_ref);
    curr_scope.define(symb);

    return node;
  }

  public override AST VisitBlock(bhlParser.BlockContext ctx)
  {
    return CommonVisitBlock(EnumBlock.SEQ, ctx.statement(), false);
  }

  public override AST VisitFuncBlock(bhlParser.FuncBlockContext ctx)
  {
    return CommonVisitBlock(EnumBlock.FUNC, ctx.block().statement(), false);
  }

  public override AST VisitParal(bhlParser.ParalContext ctx)
  {
    return CommonVisitBlock(EnumBlock.PARAL, ctx.block().statement(), false);
  }

  public override AST VisitParalAll(bhlParser.ParalAllContext ctx)
  {
    return CommonVisitBlock(EnumBlock.PARAL_ALL, ctx.block().statement(), false);
  }

  public override AST VisitPrio(bhlParser.PrioContext ctx)
  {
    return CommonVisitBlock(EnumBlock.PRIO, ctx.block().statement(), false);
  }

  public override AST VisitUntilFailure(bhlParser.UntilFailureContext ctx)
  {
    return CommonVisitBlock(EnumBlock.UNTIL_FAILURE, ctx.block().statement(), false);
  }

  public override AST VisitUntilFailure_(bhlParser.UntilFailure_Context ctx)
  {
    return CommonVisitBlock(EnumBlock.UNTIL_FAILURE_, ctx.block().statement(), false);
  }

  public override AST VisitUntilSuccess(bhlParser.UntilSuccessContext ctx)
  {
    return CommonVisitBlock(EnumBlock.UNTIL_SUCCESS, ctx.block().statement(), false);
  }

  public override AST VisitNot(bhlParser.NotContext ctx)
  {
    return CommonVisitBlock(EnumBlock.NOT, ctx.block().statement(), false);
  }

  public override AST VisitForever(bhlParser.ForeverContext ctx)
  {
    return CommonVisitBlock(EnumBlock.FOREVER, ctx.block().statement(), false);
  }

  public override AST VisitSeq(bhlParser.SeqContext ctx)
  {
    return CommonVisitBlock(EnumBlock.SEQ, ctx.block().statement(), false);
  }

  public override AST VisitSeq_(bhlParser.Seq_Context ctx)
  {
    return CommonVisitBlock(EnumBlock.SEQ_, ctx.block().statement(), false);
  }

  public override AST VisitDefer(bhlParser.DeferContext ctx)
  {
    return CommonVisitBlock(EnumBlock.DEFER, ctx.block().statement(), false);
  }

  public override AST VisitIf(bhlParser.IfContext ctx)
  {
    var node = AST_Util.New_Block(EnumBlock.IF);

    var main = ctx.mainIf();

    var main_cond = AST_Util.New_Block(EnumBlock.SEQ);
    main_cond.AddChild(Visit(main.exp()));

    SymbolTable.CheckAssign(SymbolTable._boolean, Wrap(main.exp()));

    node.AddChild(main_cond);
    node.AddChild(CommonVisitBlock(EnumBlock.SEQ, main.block().statement(), false));

    //NOTE: when inside if we reset whethe there was a return statement,
    //      this way we force the presence of return out of 'if/else' block 
    var func_symb = PeekFuncDecl().symbol;
    func_symb.return_statement_found = false;

    var else_if = ctx.elseIf();
    for(int i=0;i<else_if.Length;++i)
    {
      var item = else_if[i];
      var item_cond = AST_Util.New_Block(EnumBlock.SEQ);
      item_cond.AddChild(Visit(item.exp()));

      SymbolTable.CheckAssign(SymbolTable._boolean, Wrap(item.exp()));

      node.AddChild(item_cond);
      node.AddChild(CommonVisitBlock(EnumBlock.SEQ, item.block().statement(), false));

      func_symb.return_statement_found = false;
    }

    var @else = ctx.@else();
    if(@else != null)
    {
      func_symb.return_statement_found = false;

      node.AddChild(CommonVisitBlock(EnumBlock.SEQ, @else.block().statement(), false));
    }

    return node;
  }

  public override AST VisitWhile(bhlParser.WhileContext ctx)
  {
    var node = AST_Util.New_Block(EnumBlock.WHILE);

    ++while_stack;

    var cond = AST_Util.New_Block(EnumBlock.SEQ);
    cond.AddChild(Visit(ctx.exp()));

    SymbolTable.CheckAssign(SymbolTable._boolean, Wrap(ctx.exp()));

    node.AddChild(cond);
    node.AddChild(CommonVisitBlock(EnumBlock.SEQ, ctx.block().statement(), false));

    --while_stack;

    return node;
  }

  public override AST VisitAssign(bhlParser.AssignContext ctx)
  {
    var node = AST_Util.New_Assign();

    node.AddChild(Visit(ctx.callExp()));
    node.AddChild(Visit(ctx.exp()));

    var dst_node = Wrap(ctx.callExp());
    var src_node = Wrap(ctx.exp());

    SymbolTable.CheckAssign(dst_node, src_node);

    return node;
  }

  static bool StatementNeedsGroup(EnumBlock type, AST st) 
  {
    bool need_group = 
      type == EnumBlock.PARAL || 
      type == EnumBlock.PARAL_ALL || 
      type == EnumBlock.PRIO;

    if(!need_group)
      return false;

    if(st is AST_Block)
      return false;

    var call = st as AST_Call;
    if(call == null)
      return true;

    //NOTE: not wrapping pure FuncCallNode(s) without any chain calls
    return !((call.type == EnumCall.FUNC || call.type == EnumCall.MFUNC) && 
              call.cargs_num == call.children.Count);
  }

  AST_Block CommonVisitBlock(EnumBlock type, IParseTree[] sts, bool new_local_scope)
  {
    if(new_local_scope)
      curr_scope = new LocalScope(curr_scope); 

    var node = AST_Util.New_Block(type);

    for(int i=0;i<sts.Length;++i)
    {
      var st = Visit(sts[i]);

      if(StatementNeedsGroup(type, st))
      {
        var seq = AST_Util.New_Block(EnumBlock.GROUP);
        seq.AddChild(st);
        node.AddChild(seq);
      }
      else
        node.AddChild(st);
    }

    //NOTE: replacing last return in a function with its statement as an optimization 
    if(type == EnumBlock.FUNC && node.children.Count > 0 && node.children[node.children.Count-1] is AST_Return)
    {
      var ret = node.children[node.children.Count-1]; 
      if(ret.children.Count > 0)
        node.children[node.children.Count-1] = ret.children[0];
      for(int i=1;i<ret.children.Count;++i)
        node.children.Add(ret.children[i]);
    }

    if(new_local_scope)
      curr_scope = curr_scope.GetEnclosingScope();

    return node;
  }
}

public class Module
{
  uint id;

  public string norm_path;
  public string file_path;
  public Dictionary<string, Module> imports = new Dictionary<string, Module>(); 
  public LocalScope symbols = new LocalScope(null);

  public Module(string norm_path, string file_path)
  {
    this.norm_path = norm_path;
    this.file_path = file_path;
  }

  public uint GetId()
  {
    if(id == 0)
    {
      //Console.WriteLine("MODULE PATH " + norm_path);
      id = Hash.CRC32(norm_path);
    }
    return id;
  }
}

public class ModuleRegistry
{
  //NOTE: used for tests only
  public Dictionary<string, string> test_sources = new Dictionary<string, string>(); 

  List<string> include_path = new List<string>();
  Dictionary<string, Module> modules = new Dictionary<string, Module>(); 

  public ModuleRegistry()
  {}

  public void AddToIncludePath(string path)
  {
    include_path.Add(Util.NormalizeFilePath(path));
  }

  public List<string> GetIncludePath()
  {
    return include_path;
  }

  public Module TryGet(string fpath)
  {
    Module m = null;
    modules.TryGetValue(fpath, out m);
    return m;
  }

  public void Register(Module m)
  {
    modules.Add(m.file_path, m);
  }

  //NOTE: should be thread safe?
  public Module ImportModule(Module curr_m, GlobalScope globals, string path)
  {
    string full_path;
    string norm_path;
    ResolvePath(curr_m.file_path, path, out full_path, out norm_path);

    //Console.WriteLine("IMPORT: " + full_path + " FROM:" + curr_m.file_path);

    //1. checking repeated imports
    if(curr_m.imports.ContainsKey(full_path))
    {
      //Console.WriteLine("HIT: " + full_path);
      return null;
    }

    //2. checking global presence
    Module m = TryGet(full_path);
    if(m != null)
    {
      curr_m.imports.Add(full_path, m);
      return m;
    }

    Stream stream;

    if(test_sources.Count > 0)
    {
      var src = test_sources[full_path];
      stream = src.ToStream();
    }
    else
      stream = File.OpenRead(full_path);

    m = new Module(norm_path, full_path);
   
    AST_Builder.Source2AST(m, stream, globals, this, true/*defs.only*/);

    stream.Close();

    //Console.WriteLine("ADDING: " + full_path + " TO:" + curr_m.file_path);
    curr_m.imports.Add(full_path, m);

    Register(m);

    return m;
  }

  void ResolvePath(string self_path, string path, out string full_path, out string norm_path)
  {
    full_path = "";
    norm_path = "";

    if(path.Length == 0)
      throw new Exception("Bad path");

    //NOTE: special case for tests
    if(test_sources.Count > 0)
    {
      norm_path = path;
      full_path = path;
      return;
    }

    full_path = Util.ResolveImportPath(include_path, self_path, path);
    norm_path = FilePath2ModulePath(full_path);
  }

  public string FilePath2ModulePath(string full_path)
  {
    full_path = Util.NormalizeFilePath(full_path);

    string norm_path = "";
    for(int i=0;i<include_path.Count;++i)
    {
      var inc_path = include_path[i];
      if(full_path.IndexOf(inc_path) == 0)
      {
        norm_path = full_path.Replace(inc_path, "");
        norm_path = norm_path.Replace('\\', '/');
        //stripping .bhl extension
        norm_path = norm_path.Substring(0, norm_path.Length-4);
        //stripping initial /
        norm_path = norm_path.TrimStart('/', '\\');
        break;
      }
    }

    if(norm_path.Length == 0)
      throw new Exception("File path '" + full_path + "' was not normalized");
    return norm_path;
  }
}

public class PostProcessor
{
  public virtual bool NeedToRegen(List<string> files) { return false; }
  public virtual void PostProc(ref AST_Module result) { }
  public virtual void Finish() {}
}

public class EmptyPostProcessor : PostProcessor {}


} //namespace bhl
