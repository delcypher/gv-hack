""" vim: set sw=2 ts=2 softtabstop=2 expandtab:
This module provides a simple
    API to GPUVerify
"""
import config
import sys
import os
import subprocess
import tempfile
import shutil
import re
import logging
import traceback

#Internal logger
_logging = logging.getLogger(__name__)

# Put GPUVerify.py module in search path
sys.path.insert(0, config.GPUVERIFY_ROOT_DIR)
from GPUVerify import ErrorCodes

# Error code to message map
helpMessage = {
ErrorCodes.SUCCESS:"",
ErrorCodes.COMMAND_LINE_ERROR:"Error processing command line.",
ErrorCodes.CLANG_ERROR:"Clang could not compile your kernel to LLVM bitcode.",
ErrorCodes.OPT_ERROR:"Could not perform necessary optimisations to your kernel.",
ErrorCodes.BUGLE_ERROR:"Could not translate LLVM bitcode to Boogie.",
ErrorCodes.GPUVERIFYVCGEN_ERROR:"Could not generate invariants and/or perform two-thread abstraction.",
ErrorCodes.BOOGIE_ERROR:"",
ErrorCodes.TIMEOUT:"Verification timed out.",
ErrorCodes.CONFIGURATION_ERROR:"The web service has been incorrectly configured. Please report this issue to gpuverify-support@googlegroups.com"
}

# Observer design pattern
class GPUVerifyObserver(object):
  """
      Receive a notification of a completed GPUVerify command.
      source     : The input source code as a string
      args       : List of command line options
      returnCode : The return code given by GPUVerify
      output     : The output of the GPUVerify Tool
  """
  def receive(self, source, args, returnCode, output):
    pass

class GPUVerifyTool(object):
  """
      rootPath : Is the root directory of the GPUVerify tool ( development or deploy)
      tempDir  : Is the directory to use for temporary files. If None set then use system default.
  """
  def __init__(self, rootPath, tempDir=None):
    rootPath = os.path.abspath(rootPath)
    if tempDir:
      self.tempDir = os.path.abspath(tempDir)
      if not os.path.exists(tempDir):
        raise Exception('Path to temporary directory must exist')
    else:
      self.tempDir = None

    if not os.path.exists(rootPath):
      raise Exception('Path to GPUVerify root must exist')

    self.toolPath = os.path.join(rootPath,'GPUVerify.py')
    if not os.path.exists(self.toolPath):
      raise Exception('Could not find GPUVerify at "' + self.toolPath + '"')

    self.observers = [ ]

  def registerObserver(self, observer):
    """
        Register an observer (of type GPUVerifyObserver) that will receive notifications
        when the runCUDA() or runOpenCL() methods are executed.

    """
    if not isinstance(observer, GPUVerifyObserver):
      raise Exception("Invalid observer")
    else:
      self.observers.append(observer)

  def filterCmdArgs(self, source, args, ignoredArgs=None, additionalArgs=None):
    """
      Extract command line arguments from the first line of the source code
      that are allowed and filter them. The intention is that `args` will be
      to be passed to runOpencl() or runCUDA()

      source : The source code as a string where arguments will be extracted from.
      args   : A list that this function will populate
      ignoredArgs : If not None this list will be populated with arguments that
                    were ignored.
      additionalArgs : If not None this list be used as additional command line
                       arguments. Just like the arguments in the source code these
                       will be filtered.
    """
    if len(args) != 0:
      raise Exception("Argument list must be empty")

    firstLine=source.splitlines()[0]

    if not firstLine.startswith('//'):
      raise Exception('First line of source must have // style comment')

    foundArgs=firstLine[2:].split() # Removing the comment and then split on spaces

    if additionalArgs:
        foundArgs.extend(additionalArgs)

    #A whitelist of allowed options
    safeOptions=['--adversarial-abstraction',
                 '--array-equalities',
                 '--asymmetric-asserts',
                 r'--atomic=(r|rw|none)',
                 # '--debug', # developer option, should not be visible
                 #'--dynamic-analysis', # Note sure if safe, disable for now
                 '--equality-abstraction',
                 '--findbugs',
                 r'--loop-unwind=\d+',
                 '--math-int',
                 '--no-annotations',
                 '--no-barrier-access-checks',
                 '--no-benign',
                 '--no-constant-write-checks',
                 '--no-infer',
                 '--no-loop-predicate-invariants',
                 '--no-refinded-atomics',
                 # '--no-smart-predication', # Most likely broken, disable
                 '--no-uniformity-analysis',
                 '--only-divergence',
                 '--only-intra-group',
                 '--only-requires',
                 # '--parallel-inference', # Might bog down machine completely
                 '--time',
                 '--staged-inference',
                 # r'--scheduling=[a-z-]+', # Not sure if safe
                 '--verify',
                 # '--verbose', # developer option, should not be visible
                 r'--warp-sync=\d{1,3}',
                 # OpenCL NDRange arguments
                 r'--global_size=(\d+|\[\d+(,\d+){0,2}\])',
                 r'--local_size=(\d+|\[\d+(,\d+){0,2}\])',
                 r'--num_groups=(\d+|\[\d+(,\d+){0,2}\])',
                 # CUDA grid arguments
                 r'--blockDim=(\d+|\[\d+(,\d+){0,2}\])',
                 r'--gridDim=(\d+|\[\d+(,\d+){0,2}\])'
                ]

    for arg in foundArgs:
      matcher=None
      for option in safeOptions:
        matcher=re.match(r'^' + option + r'$',arg)
        if matcher:
          args.append(matcher.group(0))
          _logging.debug('Accepting command line option "' + args[-1] + '"')
          break
      # Warn about ignored args except the gridDim types as they are handled else where.
      if matcher == None:
        _logging.warning('Ignoring passed command line option "' + arg + '"')
        if ignoredArgs != None:
          ignoredArgs.append(arg)


  def runOpenCL(self, source, args, timeout=10):
   return self.__runCommon( source, args, '.cl', timeout)

  def __runCommon(self, source, cmdArgs, fileExtension, timeout):
    """
        This function will excute GPUVerify on source code. This function
        exists because there is a lot of common functionality between checking
        an OpenCL kernel and a CUDA kernel.

        source : The program source code to be checked as a string
        cmdArgs : A list of command line arguments to pass to GPUVerify
        fileExtension : 'cl' or 'cu'
        timeout : An integer timeout (0 is no timeout)
    """

    # Perform sanity check of timeout
    if timeout <= 0:
      raise Exception('timeout must be positive')

    cmdArgs.append("--timeout=" + str(timeout))


    # Create source file inside self.tempDir
    f = tempfile.NamedTemporaryFile(prefix='gpuverify-source-',
                                    suffix=fileExtension,
                                    delete=False,
                                    dir=self.tempDir)
    response=None
    try:
      f.write(source.encode('utf8'))
      f.close()

      # Add sourcefile name to cmdArgs
      cmdArgs.append(f.name)

      response = self.__runTool(cmdArgs)
      if response[0] == ErrorCodes.TIMEOUT:
        _logging.error('GPUVerify timed out (ErrorCode:{})'.format(response[0]))

    finally:
      f.close()
      os.remove(f.name)

    # Invoke any observers on the outcome of running the command
    for observer in self.observers:
      # Do not allow observers to cause their exceptions to cause the complete execution to fail.
      try:
        _logging.debug("Executing Observer " + str(observer.__class__))
        observer.receive( source, cmdArgs, response[0], response[1])
      except Exception as e:
        _logging.error("Observer " + str(observer.__class__) + " raised exception " + str(e) + '\n' + traceback.format_exc()  )

    return response


  def runCUDA(self, source, args, timeout=10):
    return self.__runCommon( source, args, '.cu', timeout)




  def getVersionString(self):
    ( returnCode, versionString ) = self.__runTool(['--version'])
    if returnCode == 0:

      # Parse version string
      matcher = re.search(r'local-revision\s+:\s+(\d+)',versionString)
      if not matcher:
        raise Exception('Could not parse local-revision string from "' + versionString + '"')
      localID=matcher.group(1)

      matcher = re.search(r'vcgen\s+:\s+([a-z0-9]+)',versionString)
      if not matcher:
        raise Exception('Could not parse vcgen string from "' + versionString + '"')
      changesetID=matcher.group(1)

      return (localID, changesetID)

    else:
      raise Exception('Could not get version')


  def __runTool(self, cmdLineArgs):
    # Make temporary working directory inside self.tempDir
    tempDir = tempfile.mkdtemp(prefix='gpuverify-working-directory-temp',dir=self.tempDir)

    returnCode = 0
    message=""
    try:
      cmdArgs = [ sys.executable, self.toolPath ] + cmdLineArgs
      _logging.debug('Running :' + str(cmdArgs))
      process = subprocess.Popen( cmdArgs,
                                    stdin = subprocess.PIPE,
                                    stdout = subprocess.PIPE,
                                    stderr = subprocess.STDOUT,
                                    cwd = tempDir,
                                    preexec_fn=os.setsid) # Make Sure GPUVerify can't kill us!

      message, _NOT_USED = process.communicate() # Run tool
      returnCode = process.returncode
    except OSError as e:
      returnCode=-1
      message = 'Internal error. Could not run "' + self.toolPath + '"'
    finally:
      # Remove the tempDir
      shutil.rmtree(tempDir)

    return ( returnCode, message )

