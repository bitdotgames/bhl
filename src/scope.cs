using System;
using System.Collections.Specialized;
using System.Collections.Generic;

namespace bhl {

public interface Scope
{
  HashedName GetScopeName();

  // Where to look next for symbols; superclass or enclosing scope
  Scope GetParentScope();
  // Scope in which this scope defined. For global scope, it's null
  Scope GetEnclosingScope();

  // Define a symbol in the current scope
  void define(Symbol sym);
  // Look up name in this scope or in parent scope if not here
  Symbol resolve(HashedName name);
}

public abstract class BaseScope : Scope 
{
  // null if global (outermost) scope
  protected Scope enclosing_scope;
  protected SymbolsDictionary symbols = new SymbolsDictionary();

  public BaseScope(Scope parent) 
  { 
    enclosing_scope = parent;  
  }

  public SymbolsDictionary GetMembers()
  {
    return symbols;
  }

  public Symbol resolve(HashedName name) 
  {
    Symbol s = null;
    symbols.TryGetValue(name, out s);
    if(s != null)
      return s;

    // if not here, check any enclosing scope
    if(enclosing_scope != null) 
      return enclosing_scope.resolve(name);

    return null;
  }

  public virtual void define(Symbol sym) 
  {
    if(symbols.Contains(sym.name))
      throw new UserError(sym.Location() + ": already defined symbol '" + sym.name.s + "'"); 

    symbols.Add(sym);

    sym.scope = this; // track the scope in each symbol
  }

  public void Append(BaseScope other)
  {
    var ms = other.GetMembers(); 
    for(int i=0;i<ms.Count;++i)
    {
      var s = ms[i];
      define(s);
    }
  }

  public Scope GetParentScope() { return enclosing_scope; }
  public Scope GetEnclosingScope() { return enclosing_scope; }

  public abstract HashedName GetScopeName();

  public override string ToString() { return string.Join(",", symbols.GetStringKeys().ToArray()); }

  static bool IsBuiltin(Symbol s)
  {
    return ((s is BuiltInTypeSymbol) || 
            (s is ArrayTypeSymbol)
           );
  }

  public static bool IsCompoundType(string name)
  {
    for(int i=0;i<name.Length;++i)
    {
      char c = name[i];
      if(!(Char.IsLetterOrDigit(c) || c == '_'))
        return true;
    }
    return false;
  }

#if BHL_FRONT
  public TypeRef type(bhlParser.TypeContext node)
  {
    var str = node.GetText();
    var type = resolve(str) as Type;

    if(type == null && node != null)
    {    
      if(node.fnargs() != null)
        type = new FuncType(this, node);

      if(node.ARR() != null)
      {
        //checking if it's an array of func ptrs
        if(type != null)
          type = new GenericArrayTypeSymbol(this, new TypeRef(type));
        else
          type = new GenericArrayTypeSymbol(this, node);
      }
    }

    var tr = new TypeRef();
    tr.bindings = this;
    tr.type = type;
    tr.name = str;
    tr.node = node;

    return tr;
  }

  public TypeRef type(bhlParser.RetTypeContext node)
  {
    var str = node == null ? "void" : node.GetText();
    var type = resolve(str) as Type;

    if(type == null && node != null)
    {    
      if(node.type().Length > 1)
      {
        var mtype = new MultiType();
        for(int i=0;i<node.type().Length;++i)
          mtype.items.Add(this.type(node.type()[i]));
        mtype.Update();
        type = mtype;
      }
      else
        return this.type(node.type()[0]);
    }

    var tr = new TypeRef();
    tr.bindings = this;
    tr.type = type;
    tr.name = str;
    tr.node = node;

    return tr;
  }
#endif

  Dictionary<string, TypeRef> type_cache = new Dictionary<string, TypeRef>();

  public TypeRef type(string name)
  {
    if(name.Length == 0)
      throw new Exception("Bad type: '" + name + "'");

    TypeRef tr;
    if(type_cache.TryGetValue(name, out tr))
      return tr;
    
    //let's check if the type was already explicitely defined
    var t = resolve(name) as Type;
    if(t != null)
    {
      tr = new TypeRef(t);
    }
    else
    {
#if BHL_FRONT
      if(IsCompoundType(name))
      {
        var node = Frontend.ParseType(name);
        if(node == null)
          throw new Exception("Bad type: '" + name + "'");

        if(node.type().Length == 1)
          tr = this.type(node.type()[0]);
        else
          tr = this.type(node);
      }
      else
#endif
        tr = new TypeRef(this, name);
    }

    type_cache.Add(name, tr);
    
    return tr;
  }

  public TypeRef type(Type t)
  {
    return new TypeRef(t);
  }
}

public class GlobalScope : BaseScope 
{
  public GlobalScope() 
    : base(null) 
  {}

  public override HashedName GetScopeName() { return new HashedName("global"); }
}

public class LocalScope : BaseScope 
{
  public LocalScope(Scope parent) 
    : base(parent) 
  {}

  public override HashedName GetScopeName() { return new HashedName("local"); }

  public override void define(Symbol sym) 
  {
    if(enclosing_scope != null && enclosing_scope.resolve(sym.name) != null)
      throw new UserError(sym.Location() + ": already defined symbol '" + sym.name.s + "'"); 

    base.define(sym);
  }
}

} //namespace bhl
