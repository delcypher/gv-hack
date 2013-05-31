//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Boogie;
using Microsoft.Boogie.GraphUtil;

namespace GPUVerify {

class UnstructuredRegion : IRegion {
  Graph<Block> blockGraph;
  Block header;
  Dictionary<Block, HashSet<Block>> loopNodes = new Dictionary<Block, HashSet<Block>>();
  Dictionary<Block, Block> innermostHeader = new Dictionary<Block, Block>();
  Expr guard;

  public UnstructuredRegion(Program p, Implementation impl) {
    blockGraph = p.ProcessLoops(impl);
    header = null;
    foreach (var h in blockGraph.SortHeadersByDominance()) {
      var loopNodes = new HashSet<Block>();
      foreach (var b in blockGraph.BackEdgeNodes(h))
        loopNodes.UnionWith(blockGraph.NaturalLoops(h, b));
      this.loopNodes[h] = loopNodes;
      foreach (var n in loopNodes)
        if (n != h) {
          if(!innermostHeader.ContainsKey(n)) {
            innermostHeader[n] = h;
          }
        }
    }
    guard = null;
  }

  private UnstructuredRegion(UnstructuredRegion r, Block h) {
    blockGraph = r.blockGraph;
    header = h;
    loopNodes = r.loopNodes;
    innermostHeader = r.innermostHeader;
    guard = null;
  }

  public object Identifier() {
    return header;
  }

  private HashSet<Block> SubBlocks() {
    if (header != null) {
      return loopNodes[header];
    } else {
      return blockGraph.Nodes;
    }
  }

  public IEnumerable<Cmd> Cmds() {
    foreach (var b in SubBlocks())
      foreach (Cmd c in b.Cmds)
        yield return c;
  }

  public IEnumerable<object> CmdsChildRegions() {
    if (header != null)
      foreach (Cmd c in header.Cmds)
        yield return c;
    foreach (var b in SubBlocks()) {
      Block bHeader;
      innermostHeader.TryGetValue(b, out bHeader);
      if (header == bHeader) {
        if (blockGraph.Headers.Contains(b))
          yield return new UnstructuredRegion(this, b);
        else
          foreach (Cmd c in b.Cmds)
            yield return c;
      }
    }
  }

  public IEnumerable<IRegion> SubRegions() {
    return SubBlocks().Intersect(loopNodes.Keys).Select(b => new UnstructuredRegion(this, b));
  }

  public Expr Guard() {
    if (header == null)
      return null;
    if (guard == null) {
      var backedges = blockGraph.BackEdgeNodes(header);
      if (backedges.Count() != 1)
        return null;
      var assumes = backedges.Single().Cmds.Cast<Cmd>().OfType<AssumeCmd>();
      if (assumes.Count() != 1)
        return null;
      guard = assumes.Single().Expr;
    }
    return guard;
  }

  public void AddInvariant(PredicateCmd pc) {
    header.Cmds = new CmdSeq((new Cmd[] {pc}.Concat(header.Cmds.Cast<Cmd>())).ToArray());
  }
}

}
