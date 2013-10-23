//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//

using System;
using System.Text;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.Basetypes;
using Microsoft.Boogie;

namespace DynamicAnalysis
{
    public class ExprTree : System.Collections.IEnumerable
    {
        protected Dictionary<int, HashSet<Node>> levels = new Dictionary<int, HashSet<Node>>();
        protected List<Node> nodes = new List<Node>();
        protected Node root = null;
        protected int height = 0;
        public BitVector evaluation;
        public bool unitialised = false;
        public Expr expr;

        public ExprTree(Expr expr)
        {
            this.expr = expr;
            root = Node.CreateFromExpr(expr);
            levels[0] = new HashSet<Node>();
            levels[0].Add(root);
            SetLevels(root, 0);
        }

        private void SetLevels(Node parent, int level)
        {
            nodes.Add(parent);
            int newLevel = level + 1;
            height = Math.Max(height, newLevel);
            if (!levels.ContainsKey(newLevel))
                levels[newLevel] = new HashSet<Node>();
            foreach (Node child in parent.GetChildren())
            {
                levels[newLevel].Add(child);
                SetLevels(child, newLevel);
            }
        }

        public void ClearState()
        {
            foreach (Node node in nodes)
            {
                if (!(node is LiteralNode<bool> || node is LiteralNode<BitVector>))
                    node.ClearState();
            }
            unitialised = false;
        }

        public Node Root()
        {
            return root;
        }

        public System.Collections.IEnumerator GetEnumerator()
        {
            for (int i = height - 1; i >= 0; --i)
            {
                yield return levels[i];
            }
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < height; ++i)
            {
                builder.Append(String.Format("Level {0}", i)).Append(Environment.NewLine).Append(" : ");
                foreach (Node node in levels[i])
                {
                    builder.Append(node.ToString()).Append(" : ");
                }
                builder.Append(Environment.NewLine);
            }
            return builder.ToString();
        }
    }

    public abstract class Node
    {
        protected List<Node> children = new List<Node>();
        protected Node parent = null;
        public bool uninitialised = false;

        public Node()
        {
        }

        public bool IsLeaf()
        {
            return children.Count == 0;
        }

        public List<Node> GetChildren()
        {
            return children;
        }

        public Node GetParent()
        {
            return parent;
        }

        public abstract void ClearState();

        public static Node CreateFromExpr(Expr expr)
        {
            if (expr is NAryExpr)
            {
                NAryExpr nary = expr as NAryExpr;
                if (nary.Fun is IfThenElse)
                {
                    Node one = CreateFromExpr(nary.Args[0]);
                    Node two = CreateFromExpr(nary.Args[1]);
                    Node three = CreateFromExpr(nary.Args[2]);
                    Node parent;
                    if (two is ExprNode<bool> || three is ExprNode<bool>)
                        parent = new TernaryNode<bool>(nary.Fun.FunctionName, one, two, three);
                    else
                        parent = new TernaryNode<BitVector>(nary.Fun.FunctionName, one, two, three);
                    one.parent = parent;
                    two.parent = parent;
                    three.parent = parent;
                    return parent;
                }
                else if (nary.Fun is BinaryOperator)
                {
                    Node one = CreateFromExpr(nary.Args[0]);
                    Node two = CreateFromExpr(nary.Args[1]);
                    Node parent;
                    BinaryOperator binOp = nary.Fun as BinaryOperator;
                    if (binOp.Op == BinaryOperator.Opcode.Or ||
                    binOp.Op == BinaryOperator.Opcode.And ||
                    binOp.Op == BinaryOperator.Opcode.Le ||
                    binOp.Op == BinaryOperator.Opcode.Lt ||
                    binOp.Op == BinaryOperator.Opcode.Ge ||
                    binOp.Op == BinaryOperator.Opcode.Gt ||
                    binOp.Op == BinaryOperator.Opcode.Eq ||
                    binOp.Op == BinaryOperator.Opcode.Neq ||
                    binOp.Op == BinaryOperator.Opcode.Imp)
                        parent = new BinaryNode<bool>(nary.Fun.FunctionName, one, two);
                    else
                        parent = new BinaryNode<BitVector>(nary.Fun.FunctionName, one, two);
                    one.parent = parent;
                    two.parent = parent;
                    return parent;
                }
                else if (nary.Fun is UnaryOperator)
                {
                    Node one = CreateFromExpr(nary.Args[0]);
                    UnaryNode<bool> parent = new UnaryNode<bool>(nary.Fun.FunctionName, one);
                    one.parent = parent;
                    return parent;
                }
                else if (nary.Fun is FunctionCall)
                {
                    FunctionCall call = nary.Fun as FunctionCall;
                    if (nary.Args.Count == 1)
                    {
                        Node one = CreateFromExpr(nary.Args[0]);
                        UnaryNode<BitVector> parent = new UnaryNode<BitVector>(nary.Fun.FunctionName, one);
                        one.parent = parent;
                        return parent;
                    }
                    else if (nary.Args.Count == 2)
                    {
                        Node one = CreateFromExpr(nary.Args[0]);
                        Node two = CreateFromExpr(nary.Args[1]);
                        Node parent;
                        if (call.FunctionName == "BV32_UGT" ||
                        call.FunctionName == "BV32_UGE" ||
                        call.FunctionName == "BV32_ULT" ||
                        call.FunctionName == "BV32_ULE" ||
                        call.FunctionName == "BV32_SGT" ||
                        call.FunctionName == "BV32_SGE" ||
                        call.FunctionName == "BV32_SLT" ||
                        call.FunctionName == "BV32_SLE" ||
                        call.FunctionName == "FLT32" ||
                        call.FunctionName == "FLE32" ||
                        call.FunctionName == "FGT32" ||
                        call.FunctionName == "FGE32" ||
                        call.FunctionName == "FLT64" ||
                        call.FunctionName == "FLE64" ||
                        call.FunctionName == "FGT64" ||
                        call.FunctionName == "FGE64")
                            parent = new BinaryNode<bool>(call.FunctionName, one, two);
                        else
                            parent = new BinaryNode<BitVector>(call.FunctionName, one, two);
                        one.parent = parent;
                        two.parent = parent;
                        return parent;
                    }
                    else
                    {
                        Print.ExitMessage("Unhandled number of arguments in Boogie function call with function: " + nary.Fun.FunctionName);
                    }
                }
                else if (nary.Fun is MapSelect)
                {
                    
                    Console.WriteLine(nary.Args[0].Type.ToString());
            
                    Node parent;
                    IdentifierExpr identifier = (IdentifierExpr)nary.Args[0];
                    if (nary.Type.IsBv || nary.Type.IsInt)
                        parent = new MapSymbolNode<BitVector>(identifier.Name);
                    else
                        parent = new MapSymbolNode<bool>(identifier.Name);
                    foreach (Expr index in nary.Args.GetRange(1, nary.Args.Count - 1))
                    {
                        Node child = CreateFromExpr(index);
                        parent.children.Add(child);
                        child.parent = parent;
                    }
                    return parent;
                }
                else
                    Print.VerboseMessage("Unhandled Nary expression: " + nary.Fun.GetType().ToString());
            }
            else if (expr is IdentifierExpr)
            {
                IdentifierExpr identifier = expr as IdentifierExpr;
                if (identifier.Type.IsBv || identifier.Type.IsInt)
                    return new ScalarSymbolNode<BitVector>(identifier.Name);
                else if (identifier.Type.IsBool)
                    return new ScalarSymbolNode<bool>(identifier.Name);
            }
            else if (expr is LiteralExpr)
            {
                LiteralExpr literal = expr as LiteralExpr;
                if (literal.Val is BvConst)
                {
                    BvConst bv = (BvConst)literal.Val;
                    return new LiteralNode<BitVector>(new BitVector(bv));
                }
                else if (literal.Val is BigNum)
                {
                    BigNum num = (BigNum)literal.Val;
                    return new LiteralNode<BitVector>(new BitVector(num.ToInt));
                }
                else if (literal.Val is bool)
                {
                    bool boolean = (bool)literal.Val;
                    return new LiteralNode<bool>(boolean);
                }
            }
			
            Print.ExitMessage("Unhandled expression tree: " + expr.ToString());
            return null;
        }
    }

    public class ExprNode<T> : Node
    {
        public List<T> evaluations = new List<T>();

        public override void ClearState()
        {
            evaluations.Clear();
            uninitialised = false;
        }
    }

    public class OpNode<T> : ExprNode<T>
    {
        public string op;

        public OpNode(string op):
			base()
        {
            this.op = op;
        }

        public override string ToString()
        {
            return op + " (" + typeof(T).ToString() + ")";
        }
    }

    public class TernaryNode<T> : OpNode<T>
    {
        public TernaryNode(string op, Node one, Node two, Node three):
			base(op)
        {
            children.Add(one);
            children.Add(two);
            children.Add(three);
        }
    }

    public class BinaryNode<T> : OpNode<T>
    {
        public BinaryNode(string op, Node one, Node two):
			base(op)
        {
            children.Add(one);
            children.Add(two);
        }
    }

    public class UnaryNode<T> : OpNode<T>
    {
        public UnaryNode(string op, Node one):
			base(op)
        {
            children.Add(one);
        }
    }

    public class ScalarSymbolNode<T> : ExprNode<T>
    {
        public string symbol;

        public ScalarSymbolNode(string symbol)
        {
            this.symbol = symbol;
        }

        public override string ToString()
        {
            return symbol;
        }
    }

    public class MapSymbolNode<T> : ExprNode<T>
    {
        public string basename;

        public MapSymbolNode(string basename)
        {
            this.basename = basename;
        }

        public override string ToString()
        {
            return basename;
        }
    }

    public class LiteralNode<T> : ExprNode<T>
    {
        public LiteralNode(T val)
        {
            evaluations.Add(val);
        }

        public override string ToString()
        {
            return evaluations[0].ToString();
        }
    }
}

