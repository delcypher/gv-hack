//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Microsoft.Boogie;
using Microsoft.Boogie.GraphUtil;
using Microsoft.Basetypes;
using System.Text.RegularExpressions;
using System.Diagnostics.Contracts;


namespace GPUVerify {

  public class GPUVerifyErrorReporter {

    enum ErrorMsgType {
      Error,
      Note,
      NoError
    };

    private static void ErrorWriteLine(string locInfo, string message, ErrorMsgType msgtype) {
      Contract.Requires(message != null);
      ConsoleColor col = Console.ForegroundColor;
      if (!String.IsNullOrEmpty(locInfo)) {
        Console.Error.Write(locInfo + " ");
      }

      switch (msgtype) {
        case ErrorMsgType.Error:
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.Write("error: ");
        break;
        case ErrorMsgType.Note:
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Error.Write("note: ");
        break;
        case ErrorMsgType.NoError:
        default:
        break;
      }

      Console.ForegroundColor = col;
      Console.Error.WriteLine(message);
    }

    private Implementation impl;

    internal GPUVerifyErrorReporter(Program program, string implName) {
      this.impl = program.Implementations().Where(Item => Item.Name.Equals(implName)).ToList()[0];
    }

    internal void ReportCounterexample(Counterexample error) {

      int WindowWidth;
      try {
        WindowWidth = Console.WindowWidth;
      } catch(IOException) {
        WindowWidth = 20;
      }

      for(int i = 0; i < WindowWidth; i++) {
        Console.Error.Write("-");
      }

      if (error is CallCounterexample) {
        CallCounterexample CallCex = (CallCounterexample)error;
        if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "barrier_divergence")) {
          ReportBarrierDivergence(CallCex.FailingCall);
        }
        else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "race")) {
          ReportRace(CallCex);
        }
        else {
          ReportRequiresFailure(CallCex.FailingCall, CallCex.FailingRequires);
        }
      }
      else if (error is ReturnCounterexample) {
        ReturnCounterexample ReturnCex = (ReturnCounterexample)error;
        ReportEnsuresFailure(ReturnCex.FailingEnsures);
      }
      else {
        AssertCounterexample AssertCex = (AssertCounterexample)error;
        if (AssertCex.FailingAssert is LoopInitAssertCmd) {
          ReportInvariantEntryFailure(AssertCex);
        }
        else if (AssertCex.FailingAssert is LoopInvMaintainedAssertCmd) {
          ReportInvariantMaintedFailure(AssertCex);
        }
        else if (QKeyValue.FindBoolAttribute(AssertCex.FailingAssert.Attributes, "barrier_invariant")) {
          ReportFailingBarrierInvariant(AssertCex);
        }
        else if (QKeyValue.FindBoolAttribute(AssertCex.FailingAssert.Attributes, "barrier_invariant_access_check")) {
          ReportFailingBarrierInvariantAccessCheck(AssertCex);
        }
        else if (QKeyValue.FindBoolAttribute(AssertCex.FailingAssert.Attributes, "constant_write")) {
          ReportFailingConstantWriteCheck(AssertCex);
        }
        else if (QKeyValue.FindBoolAttribute(AssertCex.FailingAssert.Attributes, "bad_pointer_access")) {
          ReportFailingBadPointerAccess(AssertCex);
        }
        else {
          ReportFailingAssert(AssertCex);
        }
      }

      DisplayParameterValues(error);

      if(((GVCommandLineOptions)CommandLineOptions.Clo).DisplayLoopAbstractions) {
        DisplayLoopAbstractions(error);
      }

    }

    private void DisplayLoopAbstractions(Counterexample error) {

      PopulateModelWithStatesIfNecessary(error);
      Program OriginalProgram = GetOriginalProgram();
      var CFG = OriginalProgram.ProcessLoops(GetOriginalImplementation(OriginalProgram));

      for(int i = 0; i < error.Trace.Count(); i++) {
        MaybeDisplayLoopHeadState(error.Trace[i], CFG, error.Model, OriginalProgram);
        if(i < error.Trace.Count() - 1) {
          MaybeDisplayLoopEntryState(error.Trace[i], error.Trace[i + 1], CFG, error.Model, OriginalProgram);
        }
        MaybeDisplayLoopBackEdgeState(error.Trace[i], CFG, error.Model, OriginalProgram);
      }
    }

    private void MaybeDisplayLoopEntryState(Block current, Block next, Graph<Block> CFG, Model Model, Program OriginalProgram) {
      var LoopHeadState = FindLoopHeadState(next);
      if(LoopHeadState == null) {
        return;
      }
      Block Header = FindLoopHeaderWithStateName(LoopHeadState, CFG);
      var LoopHeadStateSuffix = LoopHeadState.Substring("loop_head_state_".Count());
      var RelevantLoopEntryStates = GetCaptureStates(current).Where(Item => Item.Contains("loop_entry_state_" + LoopHeadStateSuffix));
      if(RelevantLoopEntryStates.Count() == 0) {
        return;
      }
      Debug.Assert(RelevantLoopEntryStates.Count() == 1);
      var LoopEntryState = RelevantLoopEntryStates.ToList()[0];
      Console.WriteLine("On entry to loop headed at " + GetSourceLocationForBasicBlock(Header).Top() + ":");
      ShowRaceInstrumentationVariables(Model, LoopEntryState, OriginalProgram);
      ShowVariablesReferencedInLoop(CFG, Model, LoopEntryState, Header);
    }

    private SourceLocationInfo GetSourceLocationForBasicBlock(Block Header) {
      foreach(var a in Header.Cmds.OfType<AssertCmd>()) {
        if(QKeyValue.FindBoolAttribute(a.Attributes, "block_sourceloc")) {
          return new SourceLocationInfo(a.Attributes, GetSourceFileName(), Header.tok);
        }
      }
      Debug.Assert(false);
      return null;
    }

    private void MaybeDisplayLoopBackEdgeState(Block current, Graph<Block> CFG, Model Model, Program OriginalProgram) {
      var RelevantLoopBackEdgeStates = GetCaptureStates(current).Where(Item => Item.Contains("loop_back_edge_state"));
      if(RelevantLoopBackEdgeStates.Count() == 0) {
        return;
      }
      Debug.Assert(RelevantLoopBackEdgeStates.Count() == 1);
      var LoopBackEdgeState = RelevantLoopBackEdgeStates.ToList()[0];
      if(GetStateFromModel(LoopBackEdgeState, Model) == null) {
        return;
      }

      var OriginalHeader = FindHeaderForBackEdgeNode(CFG, FindNodeContainingCaptureState(CFG, LoopBackEdgeState));
      Console.WriteLine("On taking back edge to head of loop at " + 
        GetSourceLocationForBasicBlock(OriginalHeader).Top() + ":");
      ShowRaceInstrumentationVariables(Model, LoopBackEdgeState, OriginalProgram);
      ShowVariablesReferencedInLoop(CFG, Model, LoopBackEdgeState, OriginalHeader);
    }

    private void MaybeDisplayLoopHeadState(Block Header, Graph<Block> CFG, Model Model, Program OriginalProgram) {
      var StateName = FindLoopHeadState(Header);
      if(StateName == null) {
        return;
      }
      Block OriginalHeader = FindLoopHeaderWithStateName(StateName, CFG);
      Debug.Assert(Header != null);
      Console.Error.WriteLine("After 0 or more iterations of loop headed at "
        + GetSourceLocationForBasicBlock(OriginalHeader).Top() + ":");
      ShowRaceInstrumentationVariables(Model, StateName, OriginalProgram);
      ShowVariablesReferencedInLoop(CFG, Model, StateName, OriginalHeader);
    }

    private void ShowRaceInstrumentationVariables(Model Model, string CapturedState, Program OriginalProgram) {
      string thread1, thread2, group1, group2;
      GetThreadsAndGroupsFromModel(Model, out thread1, out thread2, out group1, out group2, false);
      foreach(var v in OriginalProgram.TopLevelDeclarations.OfType<Variable>().Where(Item =>
        QKeyValue.FindBoolAttribute(Item.Attributes, "race_checking"))) {
        foreach(var t in AccessType.Types) {
          if(v.Name.StartsWith("_" + t + "_HAS_OCCURRED_")) {
            string ArrayName;
            AccessType Access;
            GetArrayNameAndAccessTypeFromAccessHasOccurredVariable(v, out ArrayName, out Access);
            var AccessOffsetVar = OriginalProgram.TopLevelDeclarations.OfType<Variable>().Where(Item =>
              Item.Name == RaceInstrumentationUtil.MakeOffsetVariableName(ArrayName, Access)).ToList()[0];
            if(ExtractVariableValueFromCapturedState(v.Name, CapturedState, Model) == "true") {
              Console.Error.WriteLine("  " + Access.ToString().ToLower() + " " + Access.Direction() + " "
                + ArrayOffsetString(Model, CapturedState, v, AccessOffsetVar, ArrayName.TrimStart(new char[] { '$' }))
                + " (" + SpecificNameForThread() + " " + thread1 + ", " + SpecificNameForGroup() + " " + group1 + ")");
            }
            break;
          }
        }
      }
    }

    private void ShowVariablesReferencedInLoop(Graph<Block> CFG, Model Model, string CapturedState, Block Header) {
      string thread1, thread2, group1, group2;
      GetThreadsAndGroupsFromModel(Model, out thread1, out thread2, out group1, out group2, false);
      foreach (var v in VC.VCGen.VarsReferencedInLoop(CFG, Header).Select(Item => Item.Name).
                        Where(Item => IsOriginalProgramVariable(Item))) {
        int Id;
        var Cleaned = CleanOriginalProgramVariable(v, out Id);
        Console.Error.Write("  " + Cleaned + " = " + ExtractVariableValueFromCapturedState(v, CapturedState, Model) + " ");
        Console.Error.WriteLine(Id == 1 ? "(" + SpecificNameForThread() + " " + thread1 + ", " + SpecificNameForGroup() + " " + group1 + ")" :
                                (Id == 2 ? "(" + SpecificNameForThread() + " " + thread2 + ", " + SpecificNameForGroup() + " " + group2 + ")" :
                                          "(uniform across threads)"));
      }
      Console.Error.WriteLine();
    }

    private bool IsAccessHasOccurredVariable(Variable v) {
      if(!QKeyValue.FindBoolAttribute(v.Attributes, "race_checking")) {
        return false;
      }
      foreach(var a in AccessType.Types) {
        if(v.Name.StartsWith("_" + a + "_HAS_OCCURRED_")) {
          return true;
        }
      }
      return false;
    }

    private void GetArrayNameAndAccessTypeFromAccessHasOccurredVariable(Variable v, out string ArrayName, out AccessType AccessType) {
      Debug.Assert(IsAccessHasOccurredVariable(v));
      foreach(var CurrentAccessType in AccessType.Types) {
        var Prefix = "_" + CurrentAccessType + "_HAS_OCCURRED_";
        if(v.Name.StartsWith(Prefix)) {
          ArrayName = v.Name.Substring(Prefix.Count());
          AccessType = CurrentAccessType;
          return;
        }
      }
      Debug.Assert(false);
      ArrayName = null;
      AccessType = null;
    }

    private bool IsOriginalProgramVariable(string Name) {
      return Name.Count() > 0 && Name.StartsWith("$");
    }

    private Block FindNodeContainingCaptureState(Graph<Block> CFG, string CaptureState) {
      foreach(var b in CFG.Nodes) {
        foreach(var c in b.Cmds.OfType<AssumeCmd>()) {
          if(QKeyValue.FindStringAttribute(c.Attributes, "captureState") == CaptureState) {
            return b;
          }
        }
      }
      return null;
    }

    private Block FindHeaderForBackEdgeNode(Graph<Block> CFG, Block BackEdgeNode) {
      foreach(var Header in CFG.Headers) {
        foreach(var CurrentBackEdgeNode in CFG.BackEdgeNodes(Header)) {
          if(BackEdgeNode == CurrentBackEdgeNode) {
            return Header;
          }
        }
      }
      return null;
    }

    private string FindLoopHeadState(Block b) {
      var RelevantLoopHeadStates = GetCaptureStates(b).Where(Item => Item.Contains("loop_head_state"));
      if (RelevantLoopHeadStates.Count() == 0) {
        return null;
      }
      Debug.Assert(RelevantLoopHeadStates.Count() == 1);
      return RelevantLoopHeadStates.ToList()[0];
    }

    private IEnumerable<string> GetCaptureStates(Block b) {
      return b.Cmds.OfType<AssumeCmd>().Select(Item =>
        QKeyValue.FindStringAttribute(Item.Attributes, "captureState")).Where(Item => Item != null);
    }

    private void DisplayParameterValues(Counterexample error)
    {
      if (impl.InParams.Count() == 0)
      {
        return;
      }

      string funName = QKeyValue.FindStringAttribute(impl.Attributes, "source_name");
      Debug.Assert(funName != null);

      Console.Error.WriteLine("Bitwise values of parameters of '" + funName + "':");
      PopulateModelWithStatesIfNecessary(error);

      string thread1, thread2, group1, group2;
      GetThreadsAndGroupsFromModel(error.Model, out thread1, out thread2, out group1, out group2, false);
      foreach (var p in impl.InParams)
      {
        int id;
        string stripped = CleanOriginalProgramVariable(p.Name, out id);
        Console.Error.Write("  " + stripped + " = ");

        var VariableName = p.Name;

        Console.Error.Write(ExtractVariableValueFromModel(VariableName, error.Model));
        Console.Error.WriteLine(id == 1 ? " (" + SpecificNameForThread() + " " + thread1 + ", " + SpecificNameForGroup() + " " + group1 + ")" :
                               (id == 2 ? " (" + SpecificNameForThread() + " " + thread2 + ", " + SpecificNameForGroup() + " " + group2 + ")" : ""));
      }
      Console.Error.WriteLine();
    }

    private static string CleanOriginalProgramVariable(string Name, out int Id) {
      return GVUtil.StripThreadIdentifier(Name, out Id).TrimStart(new char[] { '$' })
        .Split(new char[] { '.' })[0];
    }

    private static string ExtractValueFromModelElement(Model.Element Element) {
      if (Element is Model.BitVector) {
        return ((Model.BitVector)Element).Numeral;
      } else if (Element is Model.Uninterpreted) {
        return "<irrelevant>";
      } else if (Element == null) {
        return "<null>";
      }
      return Element.ToString(); //"<unknown>";
    }

    private static string ExtractVariableValueFromCapturedState(string VariableName, string StateName, Model model) {
      return ExtractValueFromModelElement(GetStateFromModel(StateName, model).TryGet(VariableName));
    }

    private static string ExtractVariableValueFromModel(string VariableName, Model model) {
      var func = model.TryGetFunc(VariableName);
      if (func != null) {
        return ExtractValueFromModelElement(func.GetConstant());
      }
      return "<unknown>";
    }

    private void ReportRace(CallCounterexample CallCex) {

      string raceName, access1, access2;

      DetermineNatureOfRace(CallCex, out raceName, out access1, out access2);

      PopulateModelWithStatesIfNecessary(CallCex);

      string RaceyArrayName = GetArrayName(CallCex.FailingRequires);
      Debug.Assert(RaceyArrayName != null);
      string RaceyArraySourceName = GetArraySourceName(CallCex.FailingRequires);
      Debug.Assert(RaceyArraySourceName != null);

      IEnumerable<SourceLocationInfo> PossibleSourcesForFirstAccess = GetPossibleSourceLocationsForFirstAccessInRace(CallCex, RaceyArrayName, AccessType.Create(access1),
        GetStateName(CallCex));
      SourceLocationInfo SourceInfoForSecondAccess = new SourceLocationInfo(GetAttributes(CallCex.FailingCall), GetSourceFileName(), CallCex.FailingCall.tok);

      ErrorWriteLine("\n" + SourceInfoForSecondAccess.Top().GetFile() + ":", "possible " + raceName + " race on " +
        ArrayOffsetString(CallCex, RaceyArraySourceName) +
        ":\n", ErrorMsgType.Error);

      string thread1, thread2, group1, group2;
      GetThreadsAndGroupsFromModel(CallCex.Model, out thread1, out thread2, out group1, out group2, true);

      Console.Error.WriteLine(access2 + " by " + SpecificNameForThread() + " " + thread2 + " in " + SpecificNameForGroup() + " " + group2 + ", " + SourceInfoForSecondAccess.Top() + ":");
      SourceInfoForSecondAccess.PrintStackTrace();

      Console.Error.Write(access1 + " by " + SpecificNameForThread() + " " + thread1 + " in " + SpecificNameForGroup() + " " + group1 + ", ");
      if(PossibleSourcesForFirstAccess.Count() == 1) {
        Console.Error.WriteLine(PossibleSourcesForFirstAccess.ToList()[0].Top() + ":");
        PossibleSourcesForFirstAccess.ToList()[0].PrintStackTrace();
      } else if(PossibleSourcesForFirstAccess.Count() == 0) {
        Console.Error.WriteLine("from external source location\n");
      } else {
        Console.Error.WriteLine("possible sources are:");
        List<SourceLocationInfo> LocationsAsList = PossibleSourcesForFirstAccess.ToList();
        LocationsAsList.Sort(new SourceLocationInfo.SourceLocationInfoComparison());
        foreach(var sli in LocationsAsList) {
          Console.Error.WriteLine(sli.Top() + ":");
          sli.PrintStackTrace();
        }
        Console.Error.WriteLine();
      }
    }

    private static string ArrayOffsetString(CallCounterexample Cex, string RaceyArraySourceName) {
      Variable AccessOffsetVar = ExtractOffsetVar(Cex);
      Variable AccessHasOccurredVar = ExtractAccessHasOccurredVar(Cex);
      string StateName = GetStateName(Cex);
      Model Model = Cex.Model;

      return ArrayOffsetString(Model, StateName, AccessHasOccurredVar, AccessOffsetVar, RaceyArraySourceName);

    }

    private static string ArrayOffsetString(Model Model, string StateName, Variable AccessHasOccurredVar, Variable AccessOffsetVar, string RaceyArraySourceName) {
      uint ElemWidthBits = (uint)QKeyValue.FindIntAttribute(AccessHasOccurredVar.Attributes, "elem_width", int.MaxValue);
      uint SourceElemWidthBits = (uint)QKeyValue.FindIntAttribute(AccessHasOccurredVar.Attributes, "source_elem_width", int.MaxValue);
      Debug.Assert(ElemWidthBits != int.MaxValue);
      Debug.Assert(SourceElemWidthBits != int.MaxValue);
      Debug.Assert((ElemWidthBits % 8) == 0);
      Debug.Assert((SourceElemWidthBits % 8) == 0);

      uint ElemWidthBytes = ElemWidthBits / 8;
      uint SourceElemWidthBytes = SourceElemWidthBits / 8;

      var OffsetModelElement =
        (RaceInstrumentationUtil.RaceCheckingMethod == RaceCheckingMethod.STANDARD
        ? GetStateFromModel(StateName,
           Model).TryGet(AccessOffsetVar.Name)
        : Model.TryGetFunc(AccessOffsetVar.Name).GetConstant()) as Model.Number;

      ulong Offset = Convert.ToUInt64(OffsetModelElement.Numeral);
      ulong OffsetInBytes = Offset * ElemWidthBytes;

      var DimensionsString = QKeyValue.FindStringAttribute(AccessHasOccurredVar.Attributes, "source_dimensions");
      var Dimensions = DimensionsString.Split(new char[] { ',' });
      Debug.Assert(Dimensions.Count() > 0);
      Debug.Assert(Dimensions[0] == "*");

      ulong[] DimensionStrides = new ulong[Dimensions.Count()];
      DimensionStrides[Dimensions.Count() - 1] = 1;
      for(int i = Dimensions.Count() - 2; i >= 0; i--) {
        DimensionStrides[i] = DimensionStrides[i + 1] * Convert.ToUInt64(Dimensions[i + 1]);
      }

      ulong OffsetInSourceElements = OffsetInBytes / SourceElemWidthBytes;
      ulong LeftOverBytes = OffsetInBytes % SourceElemWidthBytes;

      string Result = RaceyArraySourceName;
      ulong Remainder = OffsetInSourceElements;
      foreach(var Stride in DimensionStrides) {
        Result += "[" + (Remainder / Stride) + "]";
        Remainder = Remainder % Stride;
      }

      if (ElemWidthBytes != SourceElemWidthBytes) {
        if (ElemWidthBytes == 1) {
          Result += " (byte " + LeftOverBytes + ")";
        } else {
          Result += " (bytes " + LeftOverBytes + ".." + (LeftOverBytes + ElemWidthBytes - 1) + ")";
        }
      }

      return Result;

    }

    private static string GetStateName(CallCounterexample CallCex)
    {
      Contract.Requires(QKeyValue.FindStringAttribute(CallCex.FailingCall.Attributes, "check_id") != null);
      string CheckId = QKeyValue.FindStringAttribute(CallCex.FailingCall.Attributes, "check_id");
      return QKeyValue.FindStringAttribute(
        (CallCex.Trace.Last().Cmds.OfType<AssumeCmd>().Where(
          Item => QKeyValue.FindStringAttribute(Item.Attributes, "check_id") == CheckId).ToList()[0]
        ).Attributes, "captureState");
    }

    private static string GetSourceFileName()
    {
      return CommandLineOptions.Clo.Files[CommandLineOptions.Clo.Files.Count() - 1];
    }

    private static void PopulateModelWithStatesIfNecessary(Counterexample Cex)
    {
      if (!Cex.ModelHasStatesAlready)
      {
        Cex.PopulateModelWithStates();
        Cex.ModelHasStatesAlready = true;
      }
    }

    private static void DetermineNatureOfRace(CallCounterexample CallCex, out string raceName, out string access1, out string access2)
    {
      if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "write_read"))
      {
        raceName = "write-read";
        access1 = "Write";
        access2 = "Read";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "read_write"))
      {
        raceName = "read-write";
        access1 = "Read";
        access2 = "Write";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "write_write"))
      {
        raceName = "write-write";
        access1 = "Write";
        access2 = "Write";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "atomic_read"))
      {
        raceName = "atomic-read";
        access1 = "Atomic";
        access2 = "Read";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "atomic_write"))
      {
        raceName = "atomic-write";
        access1 = "Atomic";
        access2 = "Write";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "read_atomic"))
      {
        raceName = "read-atomic";
        access1 = "Read";
        access2 = "Atomic";
      }
      else if (QKeyValue.FindBoolAttribute(CallCex.FailingRequires.Attributes, "write_atomic"))
      {
        raceName = "write-atomic";
        access1 = "Write";
        access2 = "Atomic";
      }
      else
      {
        Debug.Assert(false);
        raceName = null;
        access1 = null;
        access2 = null;
      }
    }

    private IEnumerable<SourceLocationInfo> GetPossibleSourceLocationsForFirstAccessInRace(CallCounterexample CallCex, string ArrayName, AccessType AccessType, string RaceyState)
    {
      string AccessHasOccurred = RaceInstrumentationUtil.MakeHasOccurredVariableName(ArrayName, AccessType);
      string AccessOffset = RaceInstrumentationUtil.MakeOffsetVariableName(ArrayName, AccessType);

      AssumeCmd ConflictingAction = DetermineConflictingAction(CallCex, RaceyState, AccessHasOccurred, AccessOffset);

      var ConflictingState = QKeyValue.FindStringAttribute(ConflictingAction.Attributes, "captureState");

      if (ConflictingState.Contains("loop_head_state"))
      {
        // The state may have been renamed (for example, if k-induction has been employed),
        // so we need to find the original state name.  This can be computed as the substring before the first
        // occurrence of '$'.  This inversion is fragile, and would be a good candidate for making robust
        string ConflictingStatePrefix;
        if(ConflictingState.Contains('$')) {
          ConflictingStatePrefix = ConflictingState.Substring(0, ConflictingState.IndexOf('$'));
        } else {
          ConflictingStatePrefix = ConflictingState;
        }
        Program originalProgram = GetOriginalProgram();
        var blockGraph = originalProgram.ProcessLoops(GetOriginalImplementation(originalProgram));
        Block header = FindLoopHeaderWithStateName(ConflictingStatePrefix, blockGraph);
        Debug.Assert(header != null);
        HashSet<Block> LoopNodes = new HashSet<Block>(
          blockGraph.BackEdgeNodes(header).Select(Item => blockGraph.NaturalLoops(header, Item)).SelectMany(Item => Item)
        );
        return GetSourceLocationsFromBlocks("_CHECK_" + AccessType + "_" + ArrayName, LoopNodes);
      }
      else if(ConflictingState.Contains("call_return_state")  ) {
        return GetSourceLocationsFromCall("_CHECK_" + AccessType + "_" + ArrayName, 
          QKeyValue.FindStringAttribute(ConflictingAction.Attributes, "procedureName"));
      } else {
        Debug.Assert(ConflictingState.Contains("check_state"));
        return new HashSet<SourceLocationInfo> { 
          new SourceLocationInfo(ConflictingAction.Attributes, GetSourceFileName(), ConflictingAction.tok)
        };
      }
    }

    private static Block FindLoopHeaderWithStateName(string StateName, Microsoft.Boogie.GraphUtil.Graph<Block> CFG) {
      foreach (var b in CFG.Headers) {
        foreach (var c in b.Cmds.OfType<AssumeCmd>()) {
          var stateId = QKeyValue.FindStringAttribute(c.Attributes, "captureState");
          if (stateId == StateName) {
            return b;
          }
        }
      }
      return null;
    }

    private Implementation GetOriginalImplementation(Program Prog) {
      return Prog.Implementations().Where(Item => Item.Name.Equals(impl.Name)).ToList()[0];
    }

    private static Program GetOriginalProgram() {
      return GVUtil.GetFreshProgram(CommandLineOptions.Clo.Files, true, true, false);
    }

    private static AssumeCmd DetermineConflictingAction(CallCounterexample CallCex, string RaceyState, string AccessHasOccurred, string AccessOffset)
    {
      AssumeCmd LastLogAssume = null;
      string LastOffsetValue = null;

      foreach (var b in CallCex.Trace)
      {
        bool finished = false;
        foreach (var c in b.Cmds.OfType<AssumeCmd>())
        {
          string StateName = QKeyValue.FindStringAttribute(c.Attributes, "captureState");
          if (StateName == null)
          {
            continue;
          }
          Model.CapturedState state = GetStateFromModel(StateName, CallCex.Model);
          if (state == null || state.TryGet(AccessHasOccurred) is Model.Uninterpreted)
          {
            // Either the state was not recorded, or the state has nothing to do with the reported error, so do not
            // analyse it further.
            continue;
          }

          Model.Boolean AHO_value = state.TryGet(AccessHasOccurred) as Model.Boolean;
          Model.BitVector AO_value = 
            (RaceInstrumentationUtil.RaceCheckingMethod == RaceCheckingMethod.STANDARD
            ? state.TryGet(AccessOffset)
            : CallCex.Model.TryGetFunc(AccessOffset).GetConstant()) as Model.BitVector;

          if (!AHO_value.Value)
          {
            LastLogAssume = null;
            LastOffsetValue = null;
          }
          else if (LastLogAssume == null || !AO_value.Numeral.Equals(LastOffsetValue))
          {
            LastLogAssume = c;
            LastOffsetValue = AO_value.Numeral;
          }
          if (StateName.Equals(RaceyState))
          {
            finished = true;
          }
          break;
        }
        if (finished)
        {
          break;
        }
      }

      Debug.Assert(LastLogAssume != null);
      return LastLogAssume;
    }

    private static IEnumerable<SourceLocationInfo> GetSourceLocationsFromCall(string CheckProcedureName, string CalleeName)
    {
      Program originalProgram = GVUtil.GetFreshProgram(CommandLineOptions.Clo.Files, true, true, false);
      var Bodies =  originalProgram.Implementations().Where(Item => Item.Name.Equals(CalleeName)).ToList();
      if(Bodies.Count == 0) {
        return new HashSet<SourceLocationInfo>();
      }
      return GetSourceLocationsFromBlocks(CheckProcedureName, Bodies[0].Blocks);
    }

    private static IEnumerable<SourceLocationInfo> GetSourceLocationsFromBlocks(string CheckProcedureName, IEnumerable<Block> Blocks)
    {
      HashSet<SourceLocationInfo> PossibleSources = new HashSet<SourceLocationInfo>();
      foreach (var c in Blocks.Select(Item => Item.Cmds).SelectMany(Item => Item).OfType<CallCmd>())
      {
        if (c.callee.Equals(CheckProcedureName))
        {
          PossibleSources.Add(new SourceLocationInfo(c.Attributes, GetSourceFileName(), c.tok));
        } else {
          foreach(var sl in GetSourceLocationsFromCall(CheckProcedureName, c.callee)) {
            PossibleSources.Add(sl);
          }
        }
      }
      return PossibleSources;
    }

    private static Model.CapturedState GetStateFromModel(string StateName, Model m)
    {
      Model.CapturedState state = null;
      foreach (var s in m.States)
      {
        if (s.Name.Equals(StateName))
        {
          state = s;
          break;
        }
      }
      return state;
    }

    private static Variable ExtractAccessHasOccurredVar(CallCounterexample err) {
      var VFV = new VariableFinderVisitor(
        RaceInstrumentationUtil.MakeHasOccurredVariableName(QKeyValue.FindStringAttribute(err.FailingRequires.Attributes, "array"), GetAccessType(err)));
      VFV.Visit(err.FailingRequires.Condition);
      return VFV.GetVariable();
    }

    private static Variable ExtractOffsetVar(CallCounterexample err) {
      var VFV = new VariableFinderVisitor(
        RaceInstrumentationUtil.MakeOffsetVariableName(QKeyValue.FindStringAttribute(err.FailingRequires.Attributes, "array"), GetAccessType(err)));
      VFV.Visit(err.FailingRequires.Condition);
      return VFV.GetVariable();
    }

    private static AccessType GetAccessType(CallCounterexample err)
    {
      if (QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "write_write") ||
          QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "write_read") ||
          QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "write_atomic"))
      {
        return AccessType.WRITE;
      }
      else if (QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "read_write") ||
               QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "read_atomic"))
      {
        return AccessType.READ;
      }
      else
      {
        Debug.Assert(QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "atomic_read") ||
                     QKeyValue.FindBoolAttribute(err.FailingRequires.Attributes, "atomic_write"));
        return AccessType.ATOMIC;
      }
    }

    static QKeyValue GetAttributes(Absy a) {
      if (a is PredicateCmd) {
        return (a as PredicateCmd).Attributes;
      }
      else if (a is Requires) {
        return (a as Requires).Attributes;
      }
      else if (a is Ensures) {
        return (a as Ensures).Attributes;
      }
      else if (a is CallCmd) {
        return (a as CallCmd).Attributes;
      }
      //Debug.Assert(false);
      return null;
    }

    private static void ReportThreadSpecificFailure(AssertCounterexample err, string messagePrefix) {
      string thread1, thread2, group1, group2;
      GetThreadsAndGroupsFromModel(err.Model, out thread1, out thread2, out group1, out group2, true);

      AssertCmd failingAssert = err.FailingAssert;

      Console.Error.WriteLine();
      var sli = new SourceLocationInfo(GetAttributes(failingAssert), GetSourceFileName(), failingAssert.tok);

      int relevantThread = QKeyValue.FindIntAttribute(GetAttributes(failingAssert), "thread", -1);
      Debug.Assert(relevantThread == 1 || relevantThread == 2);

      ErrorWriteLine(sli.Top() + ":", messagePrefix + " for " + SpecificNameForThread() + " " +
                     (relevantThread == 1 ? thread1 : thread2) + " in " + SpecificNameForGroup() + " " + (relevantThread == 1 ? group1 : group2), ErrorMsgType.Error);
      sli.PrintStackTrace();
      Console.Error.WriteLine();
    }

    private static void ReportFailingAssert(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "this assertion might not hold");
    }

    private static void ReportInvariantMaintedFailure(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "loop invariant might not be maintained by the loop");
    }

    private static void ReportInvariantEntryFailure(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "loop invariant might not hold on entry");
    }

    private static void ReportFailingBarrierInvariant(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "this barrier invariant might not hold");
    }

    private static void ReportFailingBarrierInvariantAccessCheck(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "insufficient permission may be held for evaluation of this barrier invariant");
    }

    private static void ReportFailingConstantWriteCheck(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "possible attempt to modify constant memory");
    }

    private static void ReportFailingBadPointerAccess(AssertCounterexample err) {
      ReportThreadSpecificFailure(err, "possible null pointer access");
    }

    private static void ReportEnsuresFailure(Absy node) {
      Console.Error.WriteLine();
      var sli = new SourceLocationInfo(GetAttributes(node), GetSourceFileName(), node.tok);
      ErrorWriteLine(sli.Top() + ":", "postcondition might not hold on all return paths", ErrorMsgType.Error);
      sli.PrintStackTrace();
    }

    private static void ReportBarrierDivergence(Absy node) {
      Console.Error.WriteLine();
      var sli = new SourceLocationInfo(GetAttributes(node), GetSourceFileName(), node.tok);
      ErrorWriteLine(sli.Top() + ":", "barrier may be reached by non-uniform control flow", ErrorMsgType.Error);
      sli.PrintStackTrace();
    }

    private static void ReportRequiresFailure(Absy callNode, Absy reqNode) {
      Console.Error.WriteLine();
      var CallSLI = new SourceLocationInfo(GetAttributes(callNode), GetSourceFileName(), callNode.tok);
      var RequiresSLI = new SourceLocationInfo(GetAttributes(reqNode), GetSourceFileName(), reqNode.tok);

      ErrorWriteLine(CallSLI.Top() + ":", "a precondition for this call might not hold", ErrorMsgType.Error);
      CallSLI.PrintStackTrace();

      ErrorWriteLine(RequiresSLI.Top() + ":", "this is the precondition that might not hold", ErrorMsgType.Note);
      RequiresSLI.PrintStackTrace();
    }

    private static void GetThreadsAndGroupsFromModel(Model model, out string thread1, out string thread2, out string group1, out string group2, bool withSpaces) {
      thread1 = GetThreadOne(model, withSpaces);
      thread2 = GetThreadTwo(model, withSpaces);
      group1 = GetGroup(model, withSpaces, 1);
      group2 = GetGroup(model, withSpaces, 2);
    }

    private static string GetGroup(Model model, bool withSpaces, int thread) {
      switch (((GVCommandLineOptions)CommandLineOptions.Clo).GridHighestDim) {
        case 0:
        return ""
          + GetGid(model, "x", thread);
        case 1:
        return "("
          + GetGid(model, "x", thread)
            + "," + (withSpaces ? " " : "")
            + GetGid(model, "y", thread)
            + ")";
        case 2:
        return "("
          + GetGid(model, "x", thread)
            + "," + (withSpaces ? " " : "")
            + GetGid(model, "y", thread)
            + "," + (withSpaces ? " " : "")
            + GetGid(model, "z", thread)
            + ")";
        default:
        Debug.Assert(false, "GetGroup(): Reached default case in switch over GridHighestDim.");
        return "";
      }
    }

    private static int GetGid(Model model, string dimension, int thread) {
      string name = "group_id_" + dimension;
      if(!((GVCommandLineOptions)CommandLineOptions.Clo).OnlyIntraGroupRaceChecking) {
        name += "$" + thread;
      }

      return model.TryGetFunc(name).GetConstant().AsInt();
    }

    private static string GetThreadTwo(Model model, bool withSpaces) {
      switch (((GVCommandLineOptions)CommandLineOptions.Clo).BlockHighestDim) {
        case 0:
        return ""
          + GetLidX2(model);
        case 1:
        return "("
          + GetLidX2(model)
            + "," + (withSpaces ? " " : "")
            + GetLidY2(model)
            + ")";
        case 2:
        return "("
          + GetLidX2(model)
            + "," + (withSpaces ? " " : "")
            + GetLidY2(model)
            + "," + (withSpaces ? " " : "")
            + GetLidZ2(model)
            + ")";
        default:
        Debug.Assert(false, "GetThreadTwo(): Reached default case in switch over BlockHighestDim.");
        return "";
      }
    }


    private static int GetLidZ2(Model model) {
      return model.TryGetFunc("local_id_z$2").GetConstant().AsInt();
    }

    private static int GetLidY2(Model model) {
      return model.TryGetFunc("local_id_y$2").GetConstant().AsInt();
    }

    private static int GetLidX2(Model model) {
      return model.TryGetFunc("local_id_x$2").GetConstant().AsInt();
    }

    private static string GetThreadOne(Model model, bool withSpaces) {
      switch (((GVCommandLineOptions)CommandLineOptions.Clo).BlockHighestDim) {
        case 0:
        return "" 
          + model.TryGetFunc("local_id_x$1").GetConstant().AsInt();
        case 1:
        return "("
          + model.TryGetFunc("local_id_x$1").GetConstant().AsInt()
            + "," + (withSpaces ? " " : "")
            + model.TryGetFunc("local_id_y$1").GetConstant().AsInt()
            + ")";
      case 2:
        return "("
          + model.TryGetFunc("local_id_x$1").GetConstant().AsInt()
            + "," + (withSpaces ? " " : "")
            + model.TryGetFunc("local_id_y$1").GetConstant().AsInt()
            + "," + (withSpaces ? " " : "")
            + model.TryGetFunc("local_id_z$1").GetConstant().AsInt()
            + ")";
        default:
        Debug.Assert(false, "GetThreadOne(): Reached default case in switch over BlockHighestDim.");
        return "";
      }
    }

    private string GetArrayName(Requires requires) {
      string arrName = QKeyValue.FindStringAttribute(requires.Attributes, "array");
      Debug.Assert(arrName != null);
      Debug.Assert(arrName.StartsWith("$$"));
      return arrName;
    }

    private string GetArraySourceName(Requires requires) {
      string arrName = QKeyValue.FindStringAttribute(requires.Attributes, "source_name");
      Debug.Assert(arrName != null);
      return arrName;
    }

    private static string SpecificNameForGroup() {
      if(((GVCommandLineOptions)CommandLineOptions.Clo).SourceLanguage == SourceLanguage.CUDA) {
        return "block";
      } else {
        return "work group";
      }
    }

    private static string SpecificNameForThread() {
      if(((GVCommandLineOptions)CommandLineOptions.Clo).SourceLanguage == SourceLanguage.CUDA) {
        return "thread";
      } else {
        return "work item";
      }
    }

  }

}
