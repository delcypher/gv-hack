﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Boogie;
using System.Diagnostics;

namespace GPUVerify
{

    class UniformityAnalyser
    {
        private GPUVerifier verifier;

        private bool ProcedureChanged;

        private Dictionary<string, KeyValuePair<bool, Dictionary<string, bool>>> uniformityInfo;

        private Dictionary<string, List<string>> inParameters;

        private Dictionary<string, List<string>> outParameters;

        public UniformityAnalyser(GPUVerifier verifier)
        {
            this.verifier = verifier;
            uniformityInfo = new Dictionary<string, KeyValuePair<bool, Dictionary<string, bool>>>();
            inParameters = new Dictionary<string, List<string>>();
            outParameters = new Dictionary<string, List<string>>();
        }

        internal void Analyse()
        {
            foreach (Declaration D in verifier.Program.TopLevelDeclarations)
            {
                if(D is Implementation)
                {
                    bool uniformProcedure =
                        (D == verifier.KernelImplementation
                        || CommandLineOptions.DoUniformityAnalysis);

                    Implementation Impl = D as Implementation;
                    uniformityInfo.Add(Impl.Name, new KeyValuePair<bool, Dictionary<string, bool>>
                        (uniformProcedure, new Dictionary<string, bool> ()));

                    SetNonUniform(Impl.Name, GPUVerifier._X.Name);
                    SetNonUniform(Impl.Name, GPUVerifier._Y.Name);
                    SetNonUniform(Impl.Name, GPUVerifier._Z.Name);

                    foreach (Variable v in Impl.LocVars)
                    {
                        if (CommandLineOptions.DoUniformityAnalysis)
                        {
                            SetUniform(Impl.Name, v.Name);
                        }
                        else
                        {
                            SetNonUniform(Impl.Name, v.Name);
                        }
                    }

                    inParameters[Impl.Name] = new List<string>();

                    foreach (Variable v in Impl.InParams)
                    {
                        inParameters[Impl.Name].Add(v.Name);
                        if (CommandLineOptions.DoUniformityAnalysis)
                        {
                            SetUniform(Impl.Name, v.Name);
                        }
                        else
                        {
                            SetNonUniform(Impl.Name, v.Name);
                        }
                    }

                    outParameters[Impl.Name] = new List<string>();
                    foreach (Variable v in Impl.OutParams)
                    {
                        outParameters[Impl.Name].Add(v.Name);
                        if (CommandLineOptions.DoUniformityAnalysis)
                        {
                            SetUniform(Impl.Name, v.Name);
                        }
                        else
                        {
                            SetNonUniform(Impl.Name, v.Name);
                        }
                    }

                    ProcedureChanged = true;
                }
            }

            if (CommandLineOptions.DoUniformityAnalysis)
            {
                while (ProcedureChanged)
                {
                    ProcedureChanged = false;

                    foreach (Declaration D in verifier.Program.TopLevelDeclarations)
                    {
                        if (D is Implementation)
                        {
                            Implementation Impl = D as Implementation;
                            Analyse(Impl, uniformityInfo[Impl.Name].Key);
                        }
                    }
                }
            }

            foreach (Declaration D in verifier.Program.TopLevelDeclarations)
            {
                if (D is Implementation)
                {
                    Implementation Impl = D as Implementation;
                    if (!IsUniform (Impl.Name))
                    {
                        List<string> newIns = new List<String>();
                        newIns.Add("_P");
                        foreach (string s in inParameters[Impl.Name])
                        {
                            newIns.Add(s);
                        }
                        inParameters[Impl.Name] = newIns;
                    }
                }
            }

            if (CommandLineOptions.ShowUniformityAnalysis)
            {
                dump();
            }
        }

        private void Analyse(Implementation Impl, bool ControlFlowIsUniform)
        {
            Analyse(Impl, Impl.StructuredStmts, ControlFlowIsUniform);
        }

        private void Analyse(Implementation impl, StmtList stmtList, bool ControlFlowIsUniform)
        {
            foreach (BigBlock bb in stmtList.BigBlocks)
            {
                Analyse(impl, bb, ControlFlowIsUniform);
            }
        }

        private void Analyse(Implementation impl, BigBlock bb, bool ControlFlowIsUniform)
        {
            foreach (Cmd c in bb.simpleCmds)
            {
                if (c is AssignCmd)
                {
                    AssignCmd assignCmd = c as AssignCmd;
                    Debug.Assert(assignCmd.Lhss.Count == 1);
                    Debug.Assert(assignCmd.Rhss.Count == 1);
                    if (assignCmd.Lhss[0] is SimpleAssignLhs)
                    {
                        SimpleAssignLhs lhs = assignCmd.Lhss[0] as SimpleAssignLhs;
                        Expr rhs = assignCmd.Rhss[0];

                        if (IsUniform(impl.Name, lhs.AssignedVariable.Name) &&
                            (!ControlFlowIsUniform || !IsUniform(impl.Name, rhs)))
                        {
                            SetNonUniform(impl.Name, lhs.AssignedVariable.Name);
                        }

                    }
                }
                else if (c is CallCmd)
                {
                    CallCmd callCmd = c as CallCmd;

                    if (callCmd.callee != verifier.BarrierProcedure.Name)
                    {

                        if (!ControlFlowIsUniform)
                        {
                            if (IsUniform(callCmd.callee))
                            {
                                SetNonUniform(callCmd.callee);
                            }
                        }
                        Implementation CalleeImplementation = GetImplementation(callCmd.callee);
                        for (int i = 0; i < CalleeImplementation.InParams.Length; i++)
                        {
                            if (IsUniform(callCmd.callee, CalleeImplementation.InParams[i].Name)
                                && !IsUniform(impl.Name, callCmd.Ins[i]))
                            {
                                SetNonUniform(callCmd.callee, CalleeImplementation.InParams[i].Name);
                            }
                        }

                        for (int i = 0; i < CalleeImplementation.OutParams.Length; i++)
                        {
                            if (IsUniform(impl.Name, callCmd.Outs[i].Name)
                                && !IsUniform(callCmd.callee, CalleeImplementation.OutParams[i].Name))
                            {
                                SetNonUniform(impl.Name, callCmd.Outs[i].Name);
                            }
                        }

                    }
                }
            }

            if (bb.ec is WhileCmd)
            {
                WhileCmd wc = bb.ec as WhileCmd;
                Analyse(impl, wc.Body, ControlFlowIsUniform && IsUniform(impl.Name, wc.Guard));
            }
            else if (bb.ec is IfCmd)
            {
                IfCmd ifCmd = bb.ec as IfCmd;
                Analyse(impl, ifCmd.thn, ControlFlowIsUniform && IsUniform(impl.Name, ifCmd.Guard));
                if (ifCmd.elseBlock != null)
                {
                    Analyse(impl, ifCmd.elseBlock, ControlFlowIsUniform && IsUniform(impl.Name, ifCmd.Guard));
                }
                Debug.Assert(ifCmd.elseIf == null);
            }

        }

        private Implementation GetImplementation(string procedureName)
        {
            foreach (Declaration D in verifier.Program.TopLevelDeclarations)
            {
                if (D is Implementation && ((D as Implementation).Name == procedureName))
                {
                    return D as Implementation;
                }
            }
            Debug.Assert(false);
            return null;
        }

        private void SetNonUniform(string procedureName)
        {
            uniformityInfo[procedureName] = new KeyValuePair<bool,Dictionary<string,bool>>
                (false, uniformityInfo[procedureName].Value);
            RecordProcedureChanged();
        }

        internal bool IsUniform(string procedureName)
        {
            if (!uniformityInfo.ContainsKey(procedureName))
            {
                return false;
            }
            return uniformityInfo[procedureName].Key;
        }

        internal bool IsUniform(string procedureName, Expr expr)
        {
            UniformExpressionAnalysisVisitor visitor = new UniformExpressionAnalysisVisitor(uniformityInfo[procedureName].Value);
            visitor.VisitExpr(expr);
            return visitor.IsUniform();
        }

        internal bool IsUniform(string procedureName, string v)
        {
            if (!uniformityInfo.ContainsKey(procedureName))
            {
                return false;
            }

            if (!uniformityInfo[procedureName].Value.ContainsKey(v))
            {
                return false;
            }
            return uniformityInfo[procedureName].Value[v];
        }

        private void SetUniform(string procedureName, string v)
        {
            uniformityInfo[procedureName].Value[v] = true;
            RecordProcedureChanged();
        }

        private void RecordProcedureChanged()
        {
            ProcedureChanged = true;
        }

        private void SetNonUniform(string procedureName, string v)
        {
            uniformityInfo[procedureName].Value[v] = false;
            RecordProcedureChanged();
        }

        private void dump()
        {
            foreach (string p in uniformityInfo.Keys)
            {
                Console.WriteLine("Procedure " + p + ": "
                    + (uniformityInfo[p].Key ? "uniform" : "nonuniform"));
                foreach (string v in uniformityInfo[p].Value.Keys)
                {
                    Console.WriteLine("  " + v + ": " +
                        (uniformityInfo[p].Value[v] ? "uniform" : "nonuniform"));
                }
                Console.Write("Ins [");
                for (int i = 0; i < inParameters[p].Count; i++)
                {
                    Console.Write((i == 0 ? "" : ", ") + inParameters[p][i]);
                }
                Console.WriteLine("]");
                Console.Write("Outs [");
                for (int i = 0; i < outParameters[p].Count; i++)
                {
                    Console.Write((i == 0 ? "" : ", ") + outParameters[p][i]);
                }
                Console.WriteLine("]");
            }
        }


        internal string GetInParameter(string procName, int i)
        {
            return inParameters[procName][i];
        }

        internal string GetOutParameter(string procName, int i)
        {
            return outParameters[procName][i];
        }


        internal bool knowsOf(string p)
        {
            return uniformityInfo.ContainsKey(p);
        }

        internal void AddNonUniform(string proc, string v)
        {
            if (uniformityInfo.ContainsKey(proc))
            {
                Debug.Assert(!uniformityInfo[proc].Value.ContainsKey(v));
                uniformityInfo[proc].Value[v] = false;
            }
        }
    }

}
