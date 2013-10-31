//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Microsoft.Boogie;
using Microsoft.Basetypes;
using ConcurrentHoudini = Microsoft.Boogie.Houdini.ConcurrentHoudini;

namespace DynamicAnalysis
{
    class UnhandledException : Exception
    {
        public UnhandledException(string message)
			: base(message)
        { 
        }
    }

    public class BoogieInterpreter
    {
        private Program program;
        private BitVector[] ThreadID1 = new BitVector[3];
        private BitVector[] ThreadID2 = new BitVector[3];
        private BitVector[] GroupID1 = new BitVector[3];
        private BitVector[] GroupID2 = new BitVector[3];
        private GPU gpu = new GPU();
        private Implementation impl;
        private Random Random = new Random();
        private Memory Memory = new Memory();
        private Dictionary<Expr, ExprTree> ExprTrees = new Dictionary<Expr, ExprTree>();
        private Dictionary<string, Block> LabelToBlock = new Dictionary<string, Block>();
        private Dictionary<AssertCmd, BitVector> AssertStatus = new Dictionary<AssertCmd, BitVector>();
        private HashSet<string> KilledAsserts = new HashSet<string>();
        private Dictionary<Tuple<BitVector, BitVector, string>, BitVector> FPInterpretations = new Dictionary<Tuple<BitVector, BitVector, string>, BitVector>();
        
        public BoogieInterpreter(Program program, Tuple<int, int, int> threadIDSpec, Tuple<int, int, int> groupIDSpec)
        {
            Console.WriteLine("Falsyifying invariants with dynamic analysis...");
            this.program = program;
            EvaulateAxioms(program.TopLevelDeclarations.OfType<Axiom>());
            EvaluateGlobalVariables(program.TopLevelDeclarations.OfType<GlobalVariable>());
            Console.WriteLine(gpu.ToString());
            SetThreadIDs(threadIDSpec);
            SetGroupIDs(groupIDSpec);
            EvaluateConstants(program.TopLevelDeclarations.OfType<Constant>());			
            InterpretKernels(program.TopLevelDeclarations.OfType<Implementation>().Where(Item => QKeyValue.FindBoolAttribute(Item.Attributes, "kernel")));
            SummarizeKilledInvariants();
            Console.WriteLine("Dynamic analysis done");
        }

        private BitVector GetRandomBV(int width)
        {
            if (width == 1)
                return new BitVector(Random.Next(0, 2));
            char[] bits = new char[width];
            // Ensure the BV represents a non-negative integer
            bits[0] = '0';
            for (int i = 1; i < width; ++i)
            {
                if (Random.NextDouble() > 0.25)
                    bits[i] = '0';
                else
                    bits[i] = '1'; 
            }
            return new BitVector(new string(bits));
        }
        
        private Tuple<BitVector, BitVector> GetID (int selectedValue, int dimensionUpperBound)
        {
            if (selectedValue > -1)
            {
                if (selectedValue == int.MaxValue)
                {
                    BitVector val1 = new BitVector(dimensionUpperBound);
                    BitVector val2 = new BitVector(dimensionUpperBound);
                    return Tuple.Create(val1, val2);
                }
                else
                {
                    BitVector val1 = new BitVector(selectedValue);
                    BitVector val2 = new BitVector(selectedValue);
                    return Tuple.Create(val1, val2);
                }
            }
            else
            {
                BitVector val1 = new BitVector(Random.Next(0, dimensionUpperBound+1));
                BitVector val2 = new BitVector(Random.Next(0, dimensionUpperBound+1));
                return Tuple.Create(val1, val2);
            }
        }
        
        private void SetThreadIDs (Tuple<int, int, int> threadIDSpec)
        {
            Tuple<BitVector,BitVector> dimX = GetID(threadIDSpec.Item1, gpu.blockDim[DIMENSION.X] - 1);
            Tuple<BitVector,BitVector> dimY = GetID(threadIDSpec.Item2, gpu.blockDim[DIMENSION.Y] - 1);
            Tuple<BitVector,BitVector> dimZ = GetID(threadIDSpec.Item3, gpu.blockDim[DIMENSION.Z] - 1);
            
            ThreadID1[0] = dimX.Item1;
            ThreadID2[0] = dimX.Item2;
            ThreadID1[1] = dimY.Item1;
            ThreadID2[1] = dimY.Item2;
            ThreadID1[2] = dimZ.Item1;
            ThreadID2[2] = dimZ.Item2;    
        }
        
        private void SetGroupIDs (Tuple<int, int, int> groupIDSpec)
        {
            Tuple<BitVector,BitVector> dimX = GetID(groupIDSpec.Item1, gpu.gridDim[DIMENSION.X] - 1);
            Tuple<BitVector,BitVector> dimY = GetID(groupIDSpec.Item2, gpu.gridDim[DIMENSION.Y] - 1);
            Tuple<BitVector,BitVector> dimZ = GetID(groupIDSpec.Item3, gpu.gridDim[DIMENSION.Z] - 1);
            
            GroupID1[0] = dimX.Item1;
            GroupID2[0] = dimX.Item2;
            GroupID1[1] = dimY.Item1;
            GroupID2[1] = dimY.Item2;
            GroupID1[2] = dimZ.Item1;
            GroupID2[2] = dimZ.Item2;    
        }

        private bool IsRaceArrayOffsetVariable(string name)
        {
            return Regex.IsMatch(name, "_(WRITE|READ|ATOMIC)_OFFSET_", RegexOptions.IgnoreCase);
        }

        private ExprTree GetExprTree(Expr expr)
        {
            if (!ExprTrees.ContainsKey(expr))
                ExprTrees[expr] = new ExprTree(expr);
            ExprTrees[expr].ClearState();
            return ExprTrees[expr];
        }
        
        private void EvaulateAxioms(IEnumerable<Axiom> axioms)
        {
            foreach (Axiom axiom in axioms)
            {
                ExprTree tree = GetExprTree(axiom.Expr);
                Stack<Node> stack = new Stack<Node>();
                stack.Push(tree.Root());
                bool search = true;
                while (search && stack.Count > 0)
                {
                    Node node = stack.Pop();
                    if (node is BinaryNode<BitVector>)
                    {
                        BinaryNode<BitVector> binary = (BinaryNode<BitVector>)node;
                        if (binary.op == "==")
                        {
                            // Assume that equality is actually assignment into the variable of interest
                            search = false;
                            ScalarSymbolNode<BitVector> left = (ScalarSymbolNode<BitVector>)binary.GetChildren()[0];
                            LiteralNode<BitVector> right = (LiteralNode<BitVector>)binary.GetChildren()[1];
                            if (left.symbol == "group_size_x")
                            {
                                gpu.blockDim[DIMENSION.X] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.blockDim[DIMENSION.X]));
                            }
                            else if (left.symbol == "group_size_y")
                            {
                                gpu.blockDim[DIMENSION.Y] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.blockDim[DIMENSION.Y]));
                            }
                            else if (left.symbol == "group_size_z")
                            {
                                gpu.blockDim[DIMENSION.Z] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.blockDim[DIMENSION.Z]));
                            }
                            else if (left.symbol == "num_groups_x")
                            {
                                gpu.gridDim[DIMENSION.X] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.gridDim[DIMENSION.X]));
                            }
                            else if (left.symbol == "num_groups_y")
                            {
                                gpu.gridDim[DIMENSION.Y] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.gridDim[DIMENSION.Y]));
                            }
                            else if (left.symbol == "num_groups_z")
                            {
                                gpu.gridDim[DIMENSION.Z] = right.GetUniqueElement().ConvertToInt32();
                                Memory.Store(left.symbol, new BitVector(gpu.gridDim[DIMENSION.Z]));
                            }
                            else
                                throw new UnhandledException("Unhandled GPU axiom: " + axiom.ToString());
                        }
                    }
                    foreach (Node child in node.GetChildren())
                        stack.Push(child);
                }
            }
        }

        private void EvaluateGlobalVariables(IEnumerable<GlobalVariable> declarations)
        {
            foreach (GlobalVariable decl in declarations)
            {
                if (decl.TypedIdent.Type is MapType)
                    Memory.AddGlobalArray(decl.Name);
                if (IsRaceArrayOffsetVariable(decl.Name))
                {
                    if (QKeyValue.FindBoolAttribute(decl.Attributes, "GLOBAL"))
                        Memory.AddRaceArrayVariable(decl.Name, MemorySpace.GLOBAL);
                    else
                        Memory.AddRaceArrayVariable(decl.Name, MemorySpace.GROUP_SHARED);
                }
            }
        }

        private void EvaluateConstants(IEnumerable<Constant> constants)
        {
            foreach (Constant constant in constants)
            {
                bool existential = false;
                if (constant.CheckBooleanAttribute("existential", ref existential))
                {
                    if (existential)
                        Memory.Store(constant.Name, BitVector.True);
                    else
                        Memory.Store(constant.Name, BitVector.False);
                }
                else if (constant.Name.Equals("local_id_x$1"))
                {
                    Memory.Store(constant.Name, ThreadID1[0]);
                }
                else if (constant.Name.Equals("local_id_y$1"))
                {
                    Memory.Store(constant.Name, ThreadID1[1]);
                }
                else if (constant.Name.Equals("local_id_z$1"))
                {
                    Memory.Store(constant.Name, ThreadID1[2]);
                }
                else if (constant.Name.Equals("local_id_x$2"))
                {
                    Memory.Store(constant.Name, ThreadID2[0]);
                }
                else if (constant.Name.Equals("local_id_y$2"))
                {
                    Memory.Store(constant.Name, ThreadID2[1]);
                }
                else if (constant.Name.Equals("local_id_z$2"))
                {
                    Memory.Store(constant.Name, ThreadID2[2]);
                }
                else if (constant.Name.Equals("group_id_x$1"))
                {
                    Memory.Store(constant.Name, GroupID1[0]);
                }
                else if (constant.Name.Equals("group_id_y$1"))
                {
                    Memory.Store(constant.Name, GroupID1[1]);
                }
                else if (constant.Name.Equals("group_id_z$1"))
                {
                    Memory.Store(constant.Name, GroupID1[2]);
                }
                else if (constant.Name.Equals("group_id_x$2"))
                {
                    Memory.Store(constant.Name, GroupID2[0]);
                }
                else if (constant.Name.Equals("group_id_y$2"))
                {
                    Memory.Store(constant.Name, GroupID2[1]);
                }
                else if (constant.Name.Equals("group_id_z$2"))
                {
                    Memory.Store(constant.Name, GroupID2[2]);
                }
            }
        }

        private void InterpretKernels(IEnumerable<Implementation> implementations)
        {
            try
            {
                foreach (Implementation impl in implementations)
                {
                    Print.VerboseMessage(String.Format("Interpreting implementation '{0}'", impl.Name));
                    this.impl = impl;
                    foreach (Requires requires in impl.Proc.Requires)
                    {
                        EvaluateRequires(requires);
                    }
                    Memory.Dump();
                    foreach (Block block in impl.Blocks)
                    {
                        LabelToBlock[block.Label] = block;
                    }
                    InitialiseFormalParams(impl.InParams);
                    {
                        bool assumesHold = true;
                        Block block = impl.Blocks[0];
                        while (block != null && assumesHold)
                        {
                            assumesHold = InterpretBasicBlock(block);
                            block = TransferControl(block);
                        }
                    }
                }
            }
            catch (UnhandledException e)
            {
                Console.WriteLine(e.ToString());
                Memory.Dump();
            }
        }

        private void EvaluateRequires(Requires requires)
        {
            // The following code currently ignores requires which are implications
            ExprTree tree = new ExprTree(requires.Condition);	
            OpNode<BitVector> root = tree.Root() as OpNode<BitVector>;
            if (root != null)
            {          
                if (root.op == "==" || root.op == "!" || root.op == "&&" || root.op == "!=")
                {
                    foreach (HashSet<Node> nodes in tree)
                    {	
                        foreach (Node node in nodes)
                        {
                            if (node is ScalarSymbolNode<BitVector>)
                            {
                                // Initially assume the boolean variable holds. If it is negated this will be handled
                                // further up in the expression tree
                                ScalarSymbolNode<BitVector> scalar = (ScalarSymbolNode<BitVector>)node;
                                Memory.Store(scalar.symbol, BitVector.True);
                            }
                            else if (node is UnaryNode<BitVector>)
                            {
                                UnaryNode<BitVector> unary = node as UnaryNode<BitVector>;
                                ScalarSymbolNode<BitVector> child = (ScalarSymbolNode<BitVector>)unary.GetChildren()[0];
                                Memory.Store(child.symbol, BitVector.False);                                
                            }
                            else if (node is BinaryNode<BitVector>)
                            {
                                BinaryNode<BitVector> binary = node as BinaryNode<BitVector>;
                                if (binary.op == "==")
                                {
                                    Console.WriteLine(requires.Condition.ToString());
                                    LiteralNode<BitVector> right = binary.GetChildren()[1] as LiteralNode<BitVector>;
                                    if (right != null)
                                    {
                                        ScalarSymbolNode<BitVector> left = binary.GetChildren()[0] as ScalarSymbolNode<BitVector>;    
                                        MapSymbolNode<BitVector> left2 = binary.GetChildren()[0] as MapSymbolNode<BitVector>;
                                        if (left != null)
                                        {
                                            Memory.Store(left.symbol, right.GetUniqueElement());
                                        }
                                        else if (left2 != null)
                                        {
                                            SubscriptExpr subscriptExpr = new SubscriptExpr();
                                            foreach (ExprNode<BitVector> child in left2.GetChildren())
                                            {
                                                BitVector subscript = child.GetUniqueElement();
                                                subscriptExpr.AddIndex(subscript);
                                            }
                                            Memory.Store(left2.basename, subscriptExpr, right.GetUniqueElement());
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void InitialiseFormalParams(List<Variable> formals)
        {
            foreach (Variable v in formals)
            {
                // Only initialise formal parameters not initialised through requires clauses
                if (!Memory.Contains(v.Name))
                {
                    Print.VerboseMessage(String.Format("Formal parameter '{0}' with type '{1}' is uninitialised", v.Name, v.TypedIdent.Type.ToString()));
                    if (v.TypedIdent.Type is BvType)
                    {
                        BvType bv = (BvType)v.TypedIdent.Type;
                        BitVector initialValue = GetRandomBV(bv.Bits);
                        Memory.Store(v.Name, initialValue);
                        Print.VerboseMessage("...assigning " + initialValue.ToString());
                    }
                    else if (v.TypedIdent.Type is BasicType)
                    {
                        BasicType basic = (BasicType)v.TypedIdent.Type;
                        if (basic.IsInt)
                            Memory.Store(v.Name, GetRandomBV(32));
                        else
                            throw new UnhandledException(String.Format("Unhandled basic type '{0}'", basic.ToString()));
                    }
                    else
                        throw new UnhandledException("Unknown data type " + v.TypedIdent.Type.ToString());
                }
			}
        }

        private bool InterpretBasicBlock (Block block)
        {
            Print.DebugMessage(String.Format("==========> Entering basic block with label '{0}'", block.Label), 1);
            // Execute all the statements
            foreach (Cmd cmd in block.Cmds)
            {   
                //Console.Write(cmd.ToString());
                if (cmd is AssignCmd)
                {
                    AssignCmd assign = cmd as AssignCmd;
                    List<ExprTree> evaluations = new List<ExprTree>();
                    // First evaluate all RHS expressions
                    foreach (Expr expr in assign.Rhss)
                    { 
                        ExprTree exprTree = GetExprTree(expr);                 
                        EvaluateExprTree(exprTree);
                        evaluations.Add(exprTree);
                    }
                    // Now update the store
                    foreach (var LhsEval in assign.Lhss.Zip(evaluations))
                    {
                        if (LhsEval.Item1 is MapAssignLhs)
                        {
                            MapAssignLhs lhs = (MapAssignLhs)LhsEval.Item1;
                            SubscriptExpr subscriptExpr = new SubscriptExpr();
                            foreach (Expr index in lhs.Indexes)
                            {
                                ExprTree exprTree = GetExprTree(index);
                                EvaluateExprTree(exprTree);
                                BitVector subscript = exprTree.evaluation;
                                subscriptExpr.AddIndex(subscript);
                            }
                            ExprTree tree = LhsEval.Item2;
                            if (!tree.unitialised)
                                Memory.Store(lhs.DeepAssignedVariable.Name, subscriptExpr, tree.evaluation);
                        }
                        else
                        {
                            SimpleAssignLhs lhs = (SimpleAssignLhs)LhsEval.Item1;
                            ExprTree tree = LhsEval.Item2;
                            if (!tree.unitialised)
                                Memory.Store(lhs.AssignedVariable.Name, tree.evaluation);
                        }
                    }
                }
                else if (cmd is CallCmd)
                {
                    CallCmd call = cmd as CallCmd;
                    if (Regex.IsMatch(call.callee, "_LOG_READ_", RegexOptions.IgnoreCase))
                        LogRead(call);
                    else if (Regex.IsMatch(call.callee, "_LOG_WRITE_", RegexOptions.IgnoreCase))
                        LogWrite(call);
                    else if (Regex.IsMatch(call.callee, "_LOG_ATOMIC_", RegexOptions.IgnoreCase))
                        LogAtomic(call);
                    else if (Regex.IsMatch(call.callee, "bugle_barrier", RegexOptions.IgnoreCase))
                        Barrier(call);
                }
                else if (cmd is AssertCmd)
                {
                    AssertCmd assert = cmd as AssertCmd;
                    // Only check asserts which have attributes as these are the conjectured invariants
                    string tag = QKeyValue.FindStringAttribute(assert.Attributes, "tag");
                    if (tag != null)
                    {   
                        ExprTree tree = GetExprTree(assert.Expr);
                        if (!AssertStatus.ContainsKey(assert))
                            AssertStatus[assert] = BitVector.True;
                        if (AssertStatus[assert].Equals(BitVector.True))
                        {
                            EvaluateExprTree(tree);
                            if (!tree.unitialised && tree.evaluation.Equals(BitVector.False))
                            {
                                Console.Write("==========> FALSE " + assert.ToString());
                                AssertStatus[assert] = BitVector.False;
                                Regex r = new Regex("_[a-z][0-9]+");
                                MatchCollection matches = r.Matches(assert.ToString());
                                string BoogieVariable = null;
                                foreach (Match match in matches)
                                {
                                    foreach (Capture capture in match.Captures)
                                    {
                                        BoogieVariable = capture.Value;
                                    }
                                }
                                Print.ConditionalExitMessage(BoogieVariable != null, "Unable to find Boogie variable");
                                KilledAsserts.Add(BoogieVariable);
                                ConcurrentHoudini.RefutedAnnotation annotation = GPUVerify.GVUtil.getRefutedAnnotation(program, BoogieVariable, impl.Name);
                                ConcurrentHoudini.RefutedSharedAnnotations[BoogieVariable] = annotation;
                            }
                        }
                    }
                }
                else if (cmd is HavocCmd)
                {
                    HavocCmd havoc = cmd as HavocCmd;
                    foreach (IdentifierExpr id in havoc.Vars)
                    {
                        if (id.Type is BvType)
                        {
                            BvType bv = (BvType)id.Type;
                            Memory.Store(id.Name, GetRandomBV(bv.Bits));
                        }
                    }
                }
                else if (cmd is AssumeCmd)
                {
                    AssumeCmd assume = cmd as AssumeCmd;
                    ExprTree tree = GetExprTree(assume.Expr);
                    EvaluateExprTree(tree);
                    if (!tree.unitialised && tree.evaluation.Equals(BitVector.False))
                    {
                        Console.WriteLine("ASSUME FALSIFIED: " + assume.Expr.ToString());
                        return false;
                    }
                }
                else
                    throw new UnhandledException("Unhandled command: " + cmd.ToString());
            }
            return true;
        }
        
        private Block TransferControl (Block block)
        {
            TransferCmd transfer = block.TransferCmd;
            if (transfer is GotoCmd)
            {
                GotoCmd goto_ = transfer as GotoCmd;
                if (goto_.labelNames.Count == 1)
                {
                    string succLabel = goto_.labelNames[0];
                    Block succ = LabelToBlock[succLabel];
                    return succ;
                }
                else
                {
                    // Loop through all potential successors and find one whose guard evaluates to true
                    foreach (string succLabel in goto_.labelNames)
                    {
                        Block succ = LabelToBlock[succLabel];
                        PredicateCmd predicateCmd = (PredicateCmd)succ.Cmds[0];
                        ExprTree exprTree = GetExprTree(predicateCmd.Expr);
                        EvaluateExprTree(exprTree);
                        if (exprTree.evaluation.Equals(BitVector.True))
                            return succ;
                    }
                    throw new UnhandledException("No successor guard evaluates to true");
                }
            }
            else if (transfer is ReturnCmd)
                return null;
            throw new UnhandledException("Unhandled control transfer command: " + transfer.ToString());
        }

        private void EvaluateBinaryNode(BinaryNode<BitVector> binary)
        {
            Print.DebugMessage("Evaluating binary bv node", 10);
            ExprNode<BitVector> left = binary.GetChildren()[0] as ExprNode<BitVector>;
            ExprNode<BitVector> right = binary.GetChildren()[1] as ExprNode<BitVector>;
            foreach (BitVector lhs in left.evaluations)
            {
                foreach (BitVector rhs in right.evaluations)
                {
                    switch (binary.op)
                    {
                        case "||":
                            if (lhs.Equals(BitVector.True) || rhs.Equals(BitVector.True))
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            
                            break;
                        case "&&":
                            if (lhs.Equals(BitVector.True) && rhs.Equals(BitVector.True))
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "==>":
                            if (rhs.Equals(BitVector.True) || lhs.Equals(BitVector.False))
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "<==>":
                            if ((lhs.Equals(BitVector.True) && rhs.Equals(BitVector.True)) 
                            || (lhs.Equals(BitVector.False) && rhs.Equals(BitVector.False)))
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "BV1_XOR":
                        case "BV8_XOR":
                        case "BV16_XOR":
                        case "BV32_XOR":
                            binary.evaluations.Add(lhs ^ rhs);
                            break;
                        case "<":
                            if (lhs < rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "<=":
                            if (lhs <= rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case ">":
                            if (lhs > rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case ">=":
                            if (lhs >= rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "==":
                            if (lhs == rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "!=":
                            if (lhs != rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "BV32_ULT":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int; 
                                if (lhsUnsigned < rhsUnsigned)
                                    binary.evaluations.Add(BitVector.True);
                                else
                                    binary.evaluations.Add(BitVector.False);
                                break;
                            }
                        case "BV32_ULE":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int;
                                if (lhsUnsigned <= rhsUnsigned)
                                    binary.evaluations.Add(BitVector.True);
                                else
                                    binary.evaluations.Add(BitVector.False);
                                break;
                            }
                        case "BV32_UGT":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int; 
                                if (lhsUnsigned > rhsUnsigned)
                                    binary.evaluations.Add(BitVector.True);
                                else
                                    binary.evaluations.Add(BitVector.False);
                                break;
                            }
                        case "BV32_UGE":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int; 
                                if (lhsUnsigned >= rhsUnsigned)
                                    binary.evaluations.Add(BitVector.True);
                                else
                                    binary.evaluations.Add(BitVector.False);
                                break;
                            }
                        case "BV32_SLT":
                            if (lhs < rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "BV32_SLE":
                            if (lhs <= rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "BV32_SGT":
                            if (lhs > rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "BV32_SGE":
                            if (lhs >= rhs)
                                binary.evaluations.Add(BitVector.True);
                            else
                                binary.evaluations.Add(BitVector.False);
                            break;
                        case "FEQ32":
                        case "FEQ64":
                        case "FGE32":
                        case "FGE64":
                        case "FGT32":
                        case "FGT64":
                        case "FLE32":
                        case "FLE64":
                        case "FLT32":
                        case "FLT64":
                        case "FUNO32":
                        case "FUNO64":
                            {
                                Tuple<BitVector, BitVector, string> FPTriple = Tuple.Create(lhs, rhs, binary.op);
                                if (!FPInterpretations.ContainsKey(FPTriple))
                                {
                                    if (Random.Next(0, 2) == 0)
                                        FPInterpretations[FPTriple] = BitVector.False;
                                    else
                                        FPInterpretations[FPTriple] = BitVector.True;
                                }
                                binary.evaluations.Add(FPInterpretations[FPTriple]);
                                break;
                            }
                        case "+":
                            binary.evaluations.Add(lhs + rhs);
                            break;
                        case "-":
                            binary.evaluations.Add(lhs - rhs);
                            break;
                        case "*":
                            binary.evaluations.Add(lhs * rhs);
                            break;
                        case "/":
                            binary.evaluations.Add(lhs / rhs);
                            break;
                        case "BV32_SREM":
                            binary.evaluations.Add(lhs % rhs);
                            break;
                        case "BV32_UREM":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int; 
                                binary.evaluations.Add(lhsUnsigned % rhsUnsigned);
                                break;
                            }
                        case "BV32_SDIV":
                            binary.evaluations.Add(lhs / rhs);
                            break;
                        case "BV32_UDIV":
                            {
                                BitVector lhsUnsigned = lhs >= BitVector.Zero ? lhs : lhs & BitVector.Max32Int; 
                                BitVector rhsUnsigned = rhs >= BitVector.Zero ? rhs : rhs & BitVector.Max32Int; 
                                binary.evaluations.Add(lhsUnsigned / rhsUnsigned);
                                break;
                            }
                        case "BV32_ASHR":
                            binary.evaluations.Add(lhs >> rhs.ConvertToInt32());
                            break;
                        case "BV32_LSHR":
                            binary.evaluations.Add(BitVector.LogicalShiftRight(lhs, rhs.ConvertToInt32()));
                            break;
                        case "BV32_SHL":
                            binary.evaluations.Add(lhs << rhs.ConvertToInt32());
                            break;
                        case "BV32_ADD":
                            binary.evaluations.Add(lhs + rhs);
                            break;
                        case "BV32_SUB":
                            binary.evaluations.Add(lhs - rhs);
                            break;
                        case "BV32_MUL":
                            binary.evaluations.Add(lhs * rhs);
                            break;
                        case "BV32_DIV":
                            binary.evaluations.Add(lhs / rhs);
                            break;
                        case "BV32_AND":
                            binary.evaluations.Add(lhs & rhs);
                            break;
                        case "FADD32":
                        case "FADD64":
                        case "FSUB32":
                        case "FSUB64":
                        case "FMUL32":
                        case "FMUL64":
                        case "FDIV32":
                        case "FDIV64":
                        case "FPOW32":
                        case "FPOW64":
                            {
                                Tuple<BitVector, BitVector, string> FPTriple = Tuple.Create(lhs, rhs, binary.op);
                                if (!FPInterpretations.ContainsKey(FPTriple))
                                    FPInterpretations[FPTriple] = new BitVector(Random.Next());
                                binary.evaluations.Add(FPInterpretations[FPTriple]);
                                break;
                            }
                        default:
                            throw new UnhandledException("Unhandled bv binary op: " + binary.op);
                    }
                }
            }
        }        

        private void EvaluateExprTree(ExprTree tree)
        {			
            foreach (HashSet<Node> nodes in tree)
            {
                foreach (Node node in nodes)
                {
                    if (node is ScalarSymbolNode<BitVector>)
                    {
                        ScalarSymbolNode<BitVector> _node = node as ScalarSymbolNode<BitVector>;
                        if (IsRaceArrayOffsetVariable(_node.symbol))
                        {							
                            foreach (BitVector offset in Memory.GetRaceArrayOffsets(_node.symbol))
                            {
                                _node.evaluations.Add(offset);
                            }
                        }
                        else
                        {
                            if (!Memory.Contains(_node.symbol))
                            {
                                _node.uninitialised = true;
                            }
                            else
                            {
                                _node.evaluations.Add(Memory.GetValue(_node.symbol));
                            }
                        }
                    }
                    else if (node is MapSymbolNode<BitVector>)
                    {
                        MapSymbolNode<BitVector> _node = node as MapSymbolNode<BitVector>;
                        SubscriptExpr subscriptExpr = new SubscriptExpr();
                        foreach (ExprNode<BitVector> child in _node.GetChildren())
                        {
                            if (child.uninitialised)
                                node.uninitialised = true;
                            else
                            {
                                BitVector subscript = child.GetUniqueElement();
                                subscriptExpr.AddIndex(subscript);
                            }
                        }
                        
                        if (!node.uninitialised)
                        {
                            if (!Memory.Contains(_node.basename, subscriptExpr))
                                node.uninitialised = true;
                            else
                                _node.evaluations.Add(Memory.GetValue(_node.basename, subscriptExpr));
                        }
                    }
                    else if (node is BVExtractNode<BitVector>)
                    {
                        BVExtractNode<BitVector> _node = node as BVExtractNode<BitVector>;
                        ExprNode<BitVector> child = (ExprNode<BitVector>) _node.GetChildren()[0];
                        if (child.uninitialised)
                            node.uninitialised = true;
                        else
                        {
                            foreach (BitVector evalChild in child.evaluations)
                            {
                                BitVector eval = BitVector.Slice(evalChild, _node.high, _node.low);
                                _node.evaluations.Add(eval);
                            }
                        }   
                    }
                    else if (node is BVConcatenationNode<BitVector>)
                    {
                        BVConcatenationNode<BitVector> _node = node as BVConcatenationNode<BitVector>;
                        ExprNode<BitVector> one = (ExprNode<BitVector>)_node.GetChildren()[0];
                        ExprNode<BitVector> two = (ExprNode<BitVector>)_node.GetChildren()[1];
                        if (one.uninitialised || two.uninitialised)
                            node.uninitialised = true;
                        else
                        {
                            Print.ConditionalExitMessage(one.evaluations.Count == 1 && two.evaluations.Count == 1, "Unable to process concatentation expression because the children have mutliple evaluations");
                            BitVector eval = BitVector.Concatenate(one.GetUniqueElement(), two.GetUniqueElement());
                            _node.evaluations.Add(eval);
                        }   
                    }
                    else if (node is UnaryNode<BitVector>)
                    {
                        UnaryNode<BitVector> _node = node as UnaryNode<BitVector>;
                        ExprNode<BitVector> child = (ExprNode<BitVector>)_node.GetChildren()[0];
                        if (child.uninitialised)
                            node.uninitialised = true;
                        else
                        {
                            switch (_node.op)
                            {
                                case "!":
                                    if (child.GetUniqueElement().Equals(BitVector.True))
                                        _node.evaluations.Add(BitVector.False);
                                    else
                                        _node.evaluations.Add(BitVector.True);
                                    break;
                                case "FABS32":
                                case "FABS64":
                                case "FCOS32":
                                case "FCOS64":
                                case "FEXP32":
                                case "FEXP64":
                                case "FLOG32":
                                case "FLOG64":
                                case "FPOW32":
                                case "FPOW64":
                                case "FSIN32":
                                case "FSIN64":
                                case "FSQRT32":
                                case "FSQRT64":
                                    {
                                        Tuple<BitVector, BitVector, string> FPTriple = Tuple.Create(child.GetUniqueElement(), BitVector.Zero, _node.op);
                                        if (!FPInterpretations.ContainsKey(FPTriple))
                                            FPInterpretations[FPTriple] = new BitVector(Random.Next());
                                        _node.evaluations.Add(FPInterpretations[FPTriple]);
                                        break;
                                    }
                                case "BV1_ZEXT32":
                                case "BV8_ZEXT32":
                                case "BV16_ZEXT32":
                                    BitVector ZeroExtended = BitVector.ZeroExtend(child.GetUniqueElement(), 32);
                                    _node.evaluations.Add(ZeroExtended);                          
                                    break;
                                case "UI32_TO_FP32":
                                case "SI32_TO_FP32":
                                case "UI32_TO_FP64":
                                case "SI32_TO_FP64":
                                    _node.evaluations.Add(child.GetUniqueElement());
                                    break;
                                default:
                                    throw new UnhandledException("Unhandled bv unary op: " + _node.op);
                            }
                        }
                    }
                    else if (node is BinaryNode<BitVector>)
                    {
                        BinaryNode<BitVector> _node = (BinaryNode<BitVector>)node;
                        EvaluateBinaryNode(_node);
                        if (_node.evaluations.Count == 0)
                            _node.uninitialised = true;
                    }
                    else if (node is TernaryNode<BitVector>)
                    {
                        TernaryNode<BitVector> _node = node as TernaryNode<BitVector>;
                        ExprNode<BitVector> one = (ExprNode<BitVector>)_node.GetChildren()[0];
                        ExprNode<BitVector> two = (ExprNode<BitVector>)_node.GetChildren()[1];
                        ExprNode<BitVector> three = (ExprNode<BitVector>)_node.GetChildren()[2];
                        if (one.evaluations.Count == 0)
                            node.uninitialised = true;
                        else
                        {
                            if (one.GetUniqueElement().Equals(BitVector.True))
                            {
                                if (two.uninitialised)
                                    node.uninitialised = true;
                                else
                                    _node.evaluations.Add(two.GetUniqueElement());
                            }
                            else
                            {
                                if (three.uninitialised)
                                    node.uninitialised = true;
                                else
                                    _node.evaluations.Add(three.GetUniqueElement());
                            }
                        }
                    }
                } 
            }
            
            ExprNode<BitVector> root = tree.Root() as ExprNode<BitVector>;
            tree.unitialised = root.uninitialised;
            if (root.evaluations.Count == 1)
            {
                tree.evaluation = root.GetUniqueElement();
            }
            else
            {
                tree.evaluation = BitVector.True;
                foreach (BitVector eval in root.evaluations)
                {
                    if (eval.Equals(BitVector.False))
                    {
                        tree.evaluation = BitVector.False;
                        break;
                    }
                }
            }
        }

        private void Barrier(CallCmd call)
        {
            ExprTree groupSharedTree = GetExprTree(call.Ins[0]);
            ExprTree globalTree = GetExprTree(call.Ins[1]);
            EvaluateExprTree(groupSharedTree);
            EvaluateExprTree(globalTree);
            
            foreach (string name in Memory.GetRaceArrayVariables())
            {
                if ((Memory.IsInGlobalMemory(name) && globalTree.evaluation.Equals(BitVector.True)) ||
                    (Memory.IsInGroupSharedMemory(name) && groupSharedTree.evaluation.Equals(BitVector.True)))
                {
                    if (Memory.GetRaceArrayOffsets(name).Count > 0)
                    {
                        int dollarIndex = name.IndexOf('$');
                        Print.ConditionalExitMessage(dollarIndex >= 0, "Unable to find dollar sign");
                        string arrayName = name.Substring(dollarIndex);
                        string accessType = name.Substring(0, dollarIndex);
                        switch (accessType)
                        {
                            case "_WRITE_OFFSET_":
                                {
                                    string accessTracker = "_WRITE_HAS_OCCURRED_" + arrayName; 
                                    Memory.Store(accessTracker, BitVector.False);
                                    break;   
                                }
                            case "_READ_OFFSET_":
                                {
                                    string accessTracker = "_READ_HAS_OCCURRED_" + arrayName; 
                                    Memory.Store(accessTracker, BitVector.False);
                                    break;
                                } 
                            case "_ATOMIC_OFFSET_":
                                {
                                    string accessTracker = "_ATOMIC_HAS_OCCURRED_" + arrayName; 
                                    Memory.Store(accessTracker, BitVector.False);
                                    break;
                                }
                        }
                    }
                    Memory.ClearRaceArrayOffset(name);
                }
            }
        }

        private void LogRead(CallCmd call)
        {
            Print.DebugMessage("In log read", 10);
            int dollarIndex = call.callee.IndexOf('$');
            Print.ConditionalExitMessage(dollarIndex >= 0, "Unable to find dollar sign");
            string arrayName = call.callee.Substring(dollarIndex);
            string raceArrayOffsetName = "_READ_OFFSET_" + arrayName + "$1";
            Print.ConditionalExitMessage(Memory.HadRaceArrayVariable(raceArrayOffsetName), "Unable to find array read offset variable: " + raceArrayOffsetName);
            Expr offsetExpr = call.Ins[1];
            ExprTree tree = GetExprTree(offsetExpr);
            EvaluateExprTree(tree);
            if (!tree.unitialised)
            {
                Memory.AddRaceArrayOffset(raceArrayOffsetName, tree.evaluation);
                string accessTracker = "_READ_HAS_OCCURRED_" + arrayName; 
                Memory.Store(accessTracker, BitVector.True);
            }
        }

        private void LogWrite(CallCmd call)
        {
            Print.DebugMessage("In log write", 10);
            int dollarIndex = call.callee.IndexOf('$');
            Print.ConditionalExitMessage(dollarIndex >= 0, "Unable to find dollar sign");
            string arrayName = call.callee.Substring(dollarIndex);
            string raceArrayOffsetName = "_WRITE_OFFSET_" + arrayName + "$1";
            Print.ConditionalExitMessage(Memory.HadRaceArrayVariable(raceArrayOffsetName), "Unable to find array read offset variable: " + raceArrayOffsetName);
            Expr offsetExpr = call.Ins[1];
            ExprTree tree = GetExprTree(offsetExpr);
            EvaluateExprTree(tree);
            if (!tree.unitialised)
            {
                Memory.AddRaceArrayOffset(raceArrayOffsetName, tree.evaluation);
                string accessTracker = "_WRITE_HAS_OCCURRED_" + arrayName; 
                Memory.Store(accessTracker, BitVector.True);
            }
        }
        
        private void LogAtomic(CallCmd call)
        {
            Print.DebugMessage("In log atomic", 10);
            int dollarIndex = call.callee.IndexOf('$');
            Print.ConditionalExitMessage(dollarIndex >= 0, "Unable to find dollar sign");
            string arrayName = call.callee.Substring(dollarIndex);
            // Evaluate the offset expression 
            Expr offsetExpr = call.Ins[1];
            ExprTree offsetTree = GetExprTree(offsetExpr);
            EvaluateExprTree(offsetTree);
            // Build the subscript expression
            SubscriptExpr subscriptExpr = new SubscriptExpr();
            subscriptExpr.AddIndex(offsetTree.evaluation);
            // For now assume there is only one argument to the atomic function
            Expr argExpr = QKeyValue.FindExprAttribute(call.Attributes, "arg1");
            ExprTree argTree = GetExprTree(argExpr);
            EvaluateExprTree(argTree);
            string atomicFunction = QKeyValue.FindStringAttribute(call.Attributes, "atomic_function");
            switch (atomicFunction)
            {
                case "__atomicAdd_unsigned_int":
                    BitVector currentVal = Memory.GetValue(arrayName, subscriptExpr);
                    BitVector updatedVal = currentVal + argTree.evaluation;
                    Memory.Store(arrayName, subscriptExpr, updatedVal);
                    break;
                default:
                    throw new UnhandledException("Unable to handle atomic function: " + atomicFunction);
            }
        }
        
        private void SummarizeKilledInvariants ()
        {
            Console.WriteLine("Dynamic analysis removed:");
            foreach (string BoogieVariable in KilledAsserts)
            {
                Console.WriteLine(BoogieVariable);
            }   
        }
    }
}
