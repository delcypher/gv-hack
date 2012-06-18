using Graphing;
using Microsoft.Boogie;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace GPUVerify {

class BlockPredicator {

  GPUVerifier verifier;
  Program prog;
  Implementation impl;
  Graph<Block> blockGraph;

  bool useProcedurePredicates = true;

  Expr returnBlockId;

  LocalVariable curVar, pVar;
  IdentifierExpr cur, p, fp;
  Expr pExpr;
  Dictionary<Microsoft.Boogie.Type, IdentifierExpr> havocVars =
    new Dictionary<Microsoft.Boogie.Type, IdentifierExpr>();
  Dictionary<Block, Expr> blockIds = new Dictionary<Block, Expr>();
  HashSet<Block> doneBlocks = new HashSet<Block>();

  BlockPredicator(GPUVerifier v, Program p, Implementation i, bool upp) {
    verifier = v;
    prog = p;
    impl = i;
    useProcedurePredicates = upp;
  }

  void PredicateCmd(List<Block> blocks, Block block, Cmd cmd, out Block nextBlock) {
    if (!useProcedurePredicates && cmd is CallCmd) {
      var trueBlock = new Block();
      blocks.Add(trueBlock);
      trueBlock.Label = block.Label + ".call.true";
      trueBlock.Cmds.Add(new AssumeCmd(Token.NoToken, p));
      trueBlock.Cmds.Add(cmd);

      var falseBlock = new Block();
      blocks.Add(falseBlock);
      falseBlock.Label = block.Label + ".call.false";
      falseBlock.Cmds.Add(new AssumeCmd(Token.NoToken, Expr.Not(p)));

      var contBlock = new Block();
      blocks.Add(contBlock);
      contBlock.Label = block.Label + ".call.cont";

      block.TransferCmd =
        new GotoCmd(Token.NoToken, new BlockSeq(trueBlock, falseBlock));
      trueBlock.TransferCmd = falseBlock.TransferCmd =
        new GotoCmd(Token.NoToken, new BlockSeq(contBlock));
      nextBlock = contBlock;
    } else {
      PredicateCmd(block.Cmds, cmd);
      nextBlock = block;
    }
  }

  void PredicateCmd(CmdSeq cmdSeq, Cmd cmd) {
    if (cmd is AssignCmd) {
      var aCmd = (AssignCmd)cmd;
      cmdSeq.Add(new AssignCmd(Token.NoToken, aCmd.Lhss,
                   new List<Expr>(aCmd.Lhss.Zip(aCmd.Rhss, (lhs, rhs) =>
                     new NAryExpr(Token.NoToken,
                       new IfThenElse(Token.NoToken),
                       new ExprSeq(p, rhs, lhs.AsExpr))))));
    } else if (cmd is AssertCmd) {
      var aCmd = (AssertCmd)cmd;
      if (cmdSeq.Last() is AssignCmd &&
          cmdSeq.Cast<Cmd>().SkipEnd(1).All(c => c is AssertCmd)) {
        // This may be a loop invariant.  Make sure it continues to appear as
        // the first statement in the block.
        var assign = cmdSeq.Last();
        cmdSeq.Truncate(cmdSeq.Length-1);
        cmdSeq.Add(new AssertCmd(Token.NoToken, Expr.Imp(pExpr, aCmd.Expr)));
        cmdSeq.Add(assign);
      } else {
        cmdSeq.Add(new AssertCmd(Token.NoToken, Expr.Imp(p, aCmd.Expr)));
      }
    } else if (cmd is AssumeCmd) {
      var aCmd = (AssumeCmd)cmd;
      cmdSeq.Add(new AssumeCmd(Token.NoToken, Expr.Imp(p, aCmd.Expr)));
    } else if (cmd is HavocCmd) {
      var hCmd = (HavocCmd)cmd;
      foreach (IdentifierExpr v in hCmd.Vars) {
        Microsoft.Boogie.Type type = v.Decl.TypedIdent.Type;
        Contract.Assert(type != null);

        IdentifierExpr havocTempExpr;
        if (havocVars.ContainsKey(type)) {
          havocTempExpr = havocVars[type];
        } else {
          var havocVar = new LocalVariable(Token.NoToken,
                             new TypedIdent(Token.NoToken,
                                            "_HAVOC_" + type.ToString(), type));
          impl.LocVars.Add(havocVar);
          havocVars[type] = havocTempExpr =
            new IdentifierExpr(Token.NoToken, havocVar);
        }
        cmdSeq.Add(new HavocCmd(Token.NoToken,
                                new IdentifierExprSeq(havocTempExpr)));
        cmdSeq.Add(Cmd.SimpleAssign(Token.NoToken, v,
                                    new NAryExpr(Token.NoToken,
                                      new IfThenElse(Token.NoToken),
                                      new ExprSeq(p, havocTempExpr, v))));
      }
    } else if (cmd is CallCmd) {
      Debug.Assert(useProcedurePredicates);
      var cCmd = (CallCmd)cmd;
      cCmd.Ins.Insert(0, p);
      cmdSeq.Add(cCmd);
    } else {
      Console.WriteLine("Unsupported cmd: " + cmd.GetType().ToString());
    }
  }

  void PredicateTransferCmd(CmdSeq cmdSeq, TransferCmd cmd) {
    if (cmd is GotoCmd) {
      var gCmd = (GotoCmd)cmd;
      var targets = new List<Expr>(
        gCmd.labelTargets.Cast<Block>().Select(b => blockIds[b]));
      if (targets.Count == 1) {
        PredicateCmd(cmdSeq, Cmd.SimpleAssign(Token.NoToken, cur, targets[0]));
      } else {
        PredicateCmd(cmdSeq, new HavocCmd(Token.NoToken,
                                          new IdentifierExprSeq(cur)));
        PredicateCmd(cmdSeq, new AssumeCmd(Token.NoToken,
               targets.Select(t => (Expr)Expr.Eq(cur, t)).Aggregate(Expr.Or)));
      }

      foreach (Block b in gCmd.labelTargets) {
        if (blockGraph.Predecessors(b).Count() == 1) {
          if (!doneBlocks.Contains(b)) {
            var assumes = b.Cmds.Cast<Cmd>().TakeWhile(c => c is AssumeCmd);
            if (assumes.Count() > 0) {
              foreach (AssumeCmd aCmd in assumes) {
                cmdSeq.Add(new AssumeCmd(Token.NoToken,
                                         Expr.Imp(Expr.Eq(cur, blockIds[b]),
                                         aCmd.Expr)));
              }
              b.Cmds =
                new CmdSeq(b.Cmds.Cast<Cmd>().Skip(assumes.Count()).ToArray());
            }
          }
        }
      }
    } else if (cmd is ReturnCmd) {
      PredicateCmd(cmdSeq, Cmd.SimpleAssign(Token.NoToken, cur, returnBlockId));
    } else {
      Console.WriteLine("Unsupported cmd: " + cmd.GetType().ToString());
    }
  }

  void PredicateImplementation() {
    blockGraph = prog.ProcessLoops(impl);
    var sortedBlocks = blockGraph.LoopyTopSort();

    int blockId = 0;
    foreach (var block in impl.Blocks)
      blockIds[block] = Expr.Literal(blockId++);
    returnBlockId = Expr.Literal(blockId++);

    curVar = new LocalVariable(Token.NoToken,
                               new TypedIdent(Token.NoToken, "cur",
                                              Microsoft.Boogie.Type.Int));
    impl.LocVars.Add(curVar);
    cur = Expr.Ident(curVar);

    pVar = new LocalVariable(Token.NoToken,
                             new TypedIdent(Token.NoToken, "p",
                                            Microsoft.Boogie.Type.Bool));
    impl.LocVars.Add(pVar);
    p = Expr.Ident(pVar);

    if (useProcedurePredicates)
      fp = Expr.Ident(impl.InParams[0]);

    var newBlocks = new List<Block>();

    Block entryBlock = new Block();
    entryBlock.Label = "entry";
    entryBlock.Cmds = new CmdSeq(Cmd.SimpleAssign(Token.NoToken, cur,
                        CreateIfFPThenElse(blockIds[sortedBlocks[0].Item1],
                                           returnBlockId)));
    newBlocks.Add(entryBlock);

    var prevBlock = entryBlock;
    foreach (var n in sortedBlocks) {
      if (n.Item2) {
        var backedgeBlock = new Block();
        newBlocks.Add(backedgeBlock);

        backedgeBlock.Label = n.Item1.Label + ".backedge";
        backedgeBlock.Cmds = new CmdSeq(new AssumeCmd(Token.NoToken,
          Expr.Eq(cur, blockIds[n.Item1]),
          new QKeyValue(Token.NoToken, "backedge", new List<object>(), null)));
        backedgeBlock.TransferCmd = new GotoCmd(Token.NoToken,
                                                new BlockSeq(n.Item1));

        var tailBlock = new Block();
        newBlocks.Add(tailBlock);

        tailBlock.Label = n.Item1.Label + ".tail";
        tailBlock.Cmds = new CmdSeq(new AssumeCmd(Token.NoToken,
                                             Expr.Neq(cur, blockIds[n.Item1])));

        prevBlock.TransferCmd = new GotoCmd(Token.NoToken,
                                        new BlockSeq(backedgeBlock, tailBlock));
        prevBlock = tailBlock;
      } else {
        var runBlock = n.Item1;
        var oldCmdSeq = runBlock.Cmds;
        runBlock.Cmds = new CmdSeq();
        newBlocks.Add(runBlock);
        prevBlock.TransferCmd = new GotoCmd(Token.NoToken,
                                            new BlockSeq(runBlock));

        pExpr = Expr.Eq(cur, blockIds[runBlock]);
        if (CommandLineOptions.Inference && blockGraph.Headers.Contains(runBlock)) {
          AddUniformCandidateInvariant(runBlock.Cmds, runBlock);
          AddNonUniformCandidateInvariant(runBlock.Cmds, runBlock);
        }
        runBlock.Cmds.Add(Cmd.SimpleAssign(Token.NoToken, p, pExpr));
        var transferCmd = runBlock.TransferCmd;
        foreach (Cmd cmd in oldCmdSeq)
          PredicateCmd(newBlocks, runBlock, cmd, out runBlock);
        PredicateTransferCmd(runBlock.Cmds, transferCmd);

        prevBlock = runBlock;
        doneBlocks.Add(runBlock);
      }
    }

    prevBlock.TransferCmd = new ReturnCmd(Token.NoToken);
    impl.Blocks = newBlocks;
  }

  private Expr CreateIfFPThenElse(Expr then, Expr eElse) {
    if (useProcedurePredicates) {
      return new NAryExpr(Token.NoToken,
                 new IfThenElse(Token.NoToken),
                 new ExprSeq(fp, then, eElse));
    } else {
      return then;
    }
  }

  private void AddUniformCandidateInvariant(CmdSeq cs, Block header) {
    cs.Add(verifier.CreateCandidateInvariant(Expr.Eq(cur,
               CreateIfFPThenElse(blockIds[header], returnBlockId)),
             "uniform loop"));
  }

  private void AddNonUniformCandidateInvariant(CmdSeq cs, Block header) {
    var loopNodes = new HashSet<Block>();
    foreach (var b in blockGraph.BackEdgeNodes(header))
      loopNodes.UnionWith(blockGraph.NaturalLoops(header, b));
    var exits = new HashSet<Expr>();
    foreach (var ln in loopNodes) {
      if (ln.TransferCmd is GotoCmd) {
        var gCmd = (GotoCmd) ln.TransferCmd;
        foreach (var exit in gCmd.labelTargets.Cast<Block>()
                                 .Where(b => !loopNodes.Contains(b)))
          exits.Add(blockIds[exit]);
      }
      if (ln.TransferCmd is ReturnCmd)
        exits.Add(returnBlockId);
    }
    var curIsHeaderOrExit = exits.Aggregate((Expr)Expr.Eq(cur, blockIds[header]),
                                            (e, exit) => Expr.Or(e, Expr.Eq(cur, exit)));
    cs.Add(verifier.CreateCandidateInvariant(
             CreateIfFPThenElse(curIsHeaderOrExit, Expr.Eq(cur, returnBlockId)),
             "non-uniform loop"));
  }

  public static void Predicate(GPUVerifier v, Program p, bool useProcedurePredicates = true) {
    foreach (var decl in p.TopLevelDeclarations.ToList()) {
      if (useProcedurePredicates && decl is DeclWithFormals && !(decl is Function)) {
        var dwf = (DeclWithFormals)decl;
        var fpVar = new Formal(Token.NoToken,
                               new TypedIdent(Token.NoToken, "_P",
                                              Microsoft.Boogie.Type.Bool),
                               /*incoming=*/true);
        dwf.InParams = new VariableSeq(
          (new Variable[] {fpVar}.Concat(dwf.InParams.Cast<Variable>()))
            .ToArray());
      }
      var impl = decl as Implementation;
      if (impl != null)
        new BlockPredicator(v, p, impl, useProcedurePredicates).PredicateImplementation();
    }
  }

}

}
