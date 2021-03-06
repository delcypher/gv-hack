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
using System.Linq;
using System.Text;
using Microsoft.Boogie;
using Microsoft.Basetypes;
using System.Diagnostics;

namespace GPUVerify
{

    class ReadCollector : AccessCollector
    {

        public List<AccessRecord> accesses = new List<AccessRecord>();

        public ReadCollector(IKernelArrayInfo State)
            : base(State)
        {
        }

        public override AssignLhs VisitSimpleAssignLhs(SimpleAssignLhs node)
        {
            return node;
        }

        public override Expr VisitNAryExpr(NAryExpr node)
        {
            if (node.Fun is MapSelect)
            {
                if ((node.Fun as MapSelect).Arity > 1)
                {
                    MultiDimensionalMapError();
                }

                if(!(node.Args[0] is IdentifierExpr)) {
                  // This should only happen if the map is one of the special _USED maps for atomics
                  var NodeArgs0 = node.Args[0] as NAryExpr;
                  Debug.Assert(NodeArgs0 != null);
                  Debug.Assert(NodeArgs0.Fun is MapSelect);
                  Debug.Assert(NodeArgs0.Args[0] is IdentifierExpr);
                  Debug.Assert(((IdentifierExpr)NodeArgs0.Args[0]).Name.StartsWith("_USED"));
                  return base.VisitNAryExpr(node);
                }

                Debug.Assert(node.Args[0] is IdentifierExpr);
                var ReadVariable = (node.Args[0] as IdentifierExpr).Decl;
                var Index = node.Args[1];
                this.VisitExpr(node.Args[1]);

                if (State.ContainsNonLocalArray(ReadVariable))
                {
                    accesses.Add(new AccessRecord(ReadVariable, Index));
                }

                return node;
            }
            else
            {
                return base.VisitNAryExpr(node);
            }
        }


    }
}
