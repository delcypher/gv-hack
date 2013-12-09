﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Boogie;

namespace GPUVerify
{

  public enum RaceCheckingMethod {
    STANDARD, WATCHDOG_SINGLE, WATCHDOG_MULTIPLE
  }
  
  public class RaceInstrumentationUtil
  {

    public static RaceCheckingMethod RaceCheckingMethod = RaceCheckingMethod.STANDARD;

    public static string MakeOffsetVariableName(string Name, AccessType Access)
    {
      if(RaceCheckingMethod == RaceCheckingMethod.WATCHDOG_SINGLE) {
        return "_WATCHED_OFFSET";
      }
      if(RaceCheckingMethod == RaceCheckingMethod.WATCHDOG_MULTIPLE) {
        return "_WATCHED_OFFSET_" + Name;
      }
      return "_" + Access + "_OFFSET_" + Name;
    }

    public static string MakeHasOccurredVariableName(string Name, AccessType Access)
    {
        return "_" + Access + "_HAS_OCCURRED_" + Name;
    }

    public static Variable MakeOffsetVariable(string Name, AccessType Access, Microsoft.Boogie.Type Type)
    {
      var Ident = new TypedIdent(Token.NoToken, RaceInstrumentationUtil.MakeOffsetVariableName(Name, Access),
          Type);
      if(RaceInstrumentationUtil.RaceCheckingMethod == RaceCheckingMethod.STANDARD) {
        return new GlobalVariable(Token.NoToken, Ident);
      }
      return new Constant(Token.NoToken, Ident, false);
    }

  }
}
