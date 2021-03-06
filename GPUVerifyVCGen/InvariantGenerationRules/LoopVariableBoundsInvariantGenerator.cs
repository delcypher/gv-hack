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
using System.Diagnostics;
using Microsoft.Boogie;
using Microsoft.Basetypes;

namespace GPUVerify.InvariantGenerationRules
{
    class LoopVariableBoundsInvariantGenerator : InvariantGenerationRule
    {

        public LoopVariableBoundsInvariantGenerator(GPUVerifier verifier)
            : base(verifier)
        {

        }

        public override void GenerateCandidates(Implementation Impl, IRegion region)
        {
            var guard = region.Guard();
            if (guard != null && verifier.uniformityAnalyser.IsUniform(Impl.Name, guard))
            {
                var visitor = new VariablesOccurringInExpressionVisitor();
                visitor.VisitExpr(guard);
                foreach (Variable v in visitor.GetVariables())
                {
                    if (!verifier.ContainsNamedVariable(LoopInvariantGenerator.GetModifiedVariables(region), v.Name))
                    {
                        continue;
                    }

                    if (v.TypedIdent.Type.IsBv)
                    {
                        int BVWidth = (v.TypedIdent.Type as BvType).Bits;

                        verifier.AddCandidateInvariant(region,
                                verifier.IntRep.MakeSge(
                                new IdentifierExpr(v.tok, v),
                                verifier.Zero(BVWidth)), "deprecatedGuardNonNeg",
                                InferenceStages.BASIC_CANDIDATE_STAGE);
                    }
                }
            }
        }

    }
}
