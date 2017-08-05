﻿using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using SparkleXrm.Tasks.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SparkleXrm.Tasks
{
    public class SolutionPackagerTask : BaseTask
    {
        public string ConectionString { get; set; }
        private string _folder;
        public SolutionPackagerTask(IOrganizationService service, ITrace trace) : base(service, trace)
        {
        }

        public SolutionPackagerTask(OrganizationServiceContext ctx, ITrace trace) : base(ctx, trace)
        {
        }

        protected override void ExecuteInternal(string folder, OrganizationServiceContext ctx)
        {

            _trace.WriteLine("Searching for packager config in '{0}'", folder);
            var configs = ConfigFile.FindConfig(folder);

            foreach (var config in configs)
            {
                _trace.WriteLine("Using Config '{0}'", config.filePath);
                _folder = config.filePath;
                UnPack(ctx, config);
            }
            _trace.WriteLine("Processed {0} config(s)", configs.Count);


        }

        public void UnPack(OrganizationServiceContext ctx, ConfigFile config)
        {
            var configs = config.GetSolutionConfig(this.Profile);
            foreach (var solutionPackagerConfig in configs)
            {
                // check solution exists
                var solution = GetSolution(solutionPackagerConfig.solution_uniquename);
                var movetoFolder = Path.Combine(config.filePath, solutionPackagerConfig.packagepath);
                var unpackPath = UnPackSolution(solutionPackagerConfig);

                // Delete existing content 
                if (Directory.Exists(movetoFolder))
                {
                    Directory.Delete(movetoFolder, true);
                }
                // Copy to the package path
                DirectoryCopy(unpackPath, movetoFolder, true);
            }
        }

        public void PackAndUpload(OrganizationServiceContext ctx, ConfigFile config)
        {
            var configs = config.GetSolutionConfig(this.Profile);
            foreach (var solutionPackagerConfig in configs)
            {
                var solution = GetSolution(solutionPackagerConfig.solution_uniquename);
                var packageFolder = Path.Combine(config.filePath, solutionPackagerConfig.packagepath);
                var solutionLocation = PackSolution(config.filePath,solutionPackagerConfig, solution);

               
                // Save Solution to output location
                ImportSolution(solutionLocation);

            }
        }
        private void Diff(OrganizationServiceContext ctx, ConfigFile config)
        {
            var configs = config.GetSolutionConfig(this.Profile);
            foreach (var solutionPackagerConfig in configs)
            {
                var movetoFolder = Path.Combine(config.filePath, solutionPackagerConfig.packagepath);
                var unpackPath = UnPackSolution(solutionPackagerConfig);

                // Delete existing content 
                Directory.Delete(movetoFolder, true);

                // Copy to the package path
                DirectoryCopy(unpackPath, movetoFolder, true);
            }
        }

        public Solution GetSolution(string uniqueName)
        {
            //Check whether it already exists
            var queryCheckForSampleSolution = new QueryExpression
            {
                EntityName = SparkleXrm.Tasks.Solution.EntityLogicalName,
                ColumnSet = new ColumnSet("uniquename","version"),
                Criteria = new FilterExpression()
            };
            queryCheckForSampleSolution.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);

            //Create the solution if it does not already exist.
            var querySampleSolutionResults = this._service.RetrieveMultiple(queryCheckForSampleSolution);

            if (querySampleSolutionResults.Entities.Count == 0)
                throw new Exception(String.Format("Solution unique name '{0}' does not exist", uniqueName));

            return querySampleSolutionResults.Entities[0].ToEntity<Solution>();
        }

        private void ImportSolution(string solutionPath)
        {
            var solutionBytes = File.ReadAllBytes(solutionPath);

            var request = new ImportSolutionRequest();
            request.OverwriteUnmanagedCustomizations = true;
            request.PublishWorkflows = true;
            request.CustomizationFile = solutionBytes;
            request.ImportJobId = Guid.NewGuid();
            var asyncExecute = new ExecuteAsyncRequest()
            {
                Request = request

            };
            var response = (ExecuteAsyncResponse)_service.Execute(asyncExecute);

            var asyncoperationid = response.AsyncJobId;
            var importComplete = false;
            var importStartedOn = DateTime.Now;
            var importError = String.Empty;
            do
            {
                try
                {
                    if (DateTime.Now.Subtract(importStartedOn).Minutes > 15)
                    {
                        throw new SparkleTaskException(SparkleTaskException.ExceptionTypes.IMPORT_ERROR, "Timeout whilst uploading solution\nThe import process has timed out after 15 minutes.");
                    }

                    // Query the job until completed
                    var job = _service.Retrieve("asyncoperation", asyncoperationid, new ColumnSet(new System.String[] { "statuscode", "message","friendlymessage" }));

                    var statuscode = job.GetAttributeValue<OptionSetValue>("statuscode");

                    switch (statuscode.Value)
                    {
                        case 30:
                            importComplete = true;
                            importError = "";
                            break;
                        case 31:
                            importComplete = true;
                            importError = job.GetAttributeValue<string>("message") + "\n" + job.GetAttributeValue<string>("friendlymessage");
                            break;
                    }
                }
                catch 
                {
                   // The import job can be locked or not yet created
                   // so don't do anything and just wait...
                }
                Thread.Sleep(new TimeSpan(0, 0, 2));
            }
            while (!importComplete);

            if (!string.IsNullOrEmpty(importError))
            {
                throw new SparkleTaskException(SparkleTaskException.ExceptionTypes.IMPORT_ERROR, importError);
            }

            // Publish
            var publishRequest = new PublishAllXmlRequest();
            var publishResponse = (PublishAllXmlResponse)_service.Execute(publishRequest);
             
                
            
        }

        private string UnPackSolution(SolutionPackageConfig solutionPackagerConfig)
        {
            // Get random folder
            var targetFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            // Extract solution
            var request = new ExportSolutionRequest
            {
                SolutionName = solutionPackagerConfig.solution_uniquename,
                ExportAutoNumberingSettings = false,
                ExportCalendarSettings = false,
                ExportCustomizationSettings = false,
                ExportEmailTrackingSettings = false,
                ExportExternalApplications = false,
                ExportGeneralSettings = false,
                ExportIsvConfig = false,
                ExportMarketingSettings = false,
                ExportOutlookSynchronizationSettings = false,
                ExportRelationshipRoles = false,
                ExportSales = false,
                Managed = false
            };

            var response = (ExportSolutionResponse)_service.Execute(request);

            // Save solution 
            var solutionZipPath = Path.GetTempFileName();
            File.WriteAllBytes(solutionZipPath, response.ExportSolutionFile);
         
            var binPath = GetPackagerFolder();
            var binFolder = new FileInfo(binPath).DirectoryName;

            // Run CrmSvcUtil 
            var parameters = String.Format(@"/action:Extract /zipfile:{0} /folder:{1} /packagetype:Unmanaged /allowWrite:Yes /allowDelete:Yes /clobber /errorlevel:Verbose /nologo /log:packagerlog.txt",
                solutionZipPath,
                targetFolder
                );

            RunPackager(binPath, binFolder, parameters);

            return targetFolder;
        }

       

        private string PackSolution(string rootPath, SolutionPackageConfig solutionPackagerConfig, Solution solution)
        {
            // Get random folder
            var packageFolder = Path.Combine(rootPath, solutionPackagerConfig.packagepath);

            if (solutionPackagerConfig.increment_on_import)
            {
                // Increment version in the package to upload
                // We increment the version in CRM already incase the solution package version is not correct
                IncrementVersion(solution.Version, packageFolder);
            }

            // Save solution to the following location
            var solutionZipPath = Path.GetTempFileName();

            var binPath = GetPackagerFolder();
            var binFolder = new FileInfo(binPath).DirectoryName;

            // Run CrmSvcUtil 
            var parameters = String.Format(@"/action:Pack /zipfile:{0} /folder:{1} /packagetype:Unmanaged /errorlevel:Verbose /nologo /log:packagerlog.txt",
                solutionZipPath,
                packageFolder
                );

            RunPackager(binPath, binFolder, parameters);

            return solutionZipPath;
        }

        private static void IncrementVersion(string currentVersion, string packageFolder)
        {  
            // Update the solution.xml
            string solutionXmlPath = Path.Combine(packageFolder, @"Other\Solution.xml");
            XDocument document;

            using (var stream = new StreamReader(solutionXmlPath))
            {
                document = XDocument.Load(stream);
            }

            var versionNode = document.Root
                        .Descendants()
                        .Where(element => element.Name == "Version")
                        .First();

            // Increment the last part of the build version
            var parts = currentVersion.Split('.');
            int buildVersion = 0;
            if (int.TryParse(parts[parts.Length - 1], out buildVersion))
            {
                buildVersion++;
                parts[parts.Length - 1] = buildVersion.ToString();
                versionNode.Value = string.Join(".", parts);
                document.Save(solutionXmlPath, SaveOptions.None);
            }
            else
            {
                throw new Exception(string.Format("Could not increment version '{0}'", currentVersion));
            }
        }

        private string GetPackagerFolder()
        {
            // locate the CrmSvcUtil package folder
            var targetfolder = DirectoryEx.GetApplicationDirectory();

            // If we are running in VS, then move up past bin/Debug
            if (targetfolder.Contains(@"bin\Debug") || targetfolder.Contains(@"bin\Release"))
            {
                targetfolder += @"\..";
            }

            // move from spkl.v.v.v.\tools - back to packages folder
            var binPath = DirectoryEx.Search(targetfolder + @"\..\..", "SolutionPackager.exe");
            _trace.WriteLine("Target {0}", targetfolder);
            
            if (string.IsNullOrEmpty(binPath))
            {
                throw new SparkleTaskException(SparkleTaskException.ExceptionTypes.UTILSNOTFOUND, String.Format("Cannot locate SolutionPackager at '{0}' - run Install-Package Microsoft.CrmSdk.CoreTools", binPath));
            }
            return binPath;
        }

        private void RunPackager(string binPath, string workingFolder, string parameters)
        {
            var procStart = new ProcessStartInfo(binPath, parameters)
            {
                WorkingDirectory = workingFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _trace.WriteLine("Running {0} {1}", binPath, parameters);

            Process proc = null;
            var exitCode = 0;
            try
            {
                proc = Process.Start(procStart);
                proc.OutputDataReceived += Proc_OutputDataReceived;
                proc.ErrorDataReceived += Proc_OutputDataReceived;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit(20 * 60 * 60 * 1000);
                proc.CancelOutputRead();
                proc.CancelErrorRead();
            }
            finally
            {
                exitCode = proc.ExitCode;
                proc.Close();
            }
            if (exitCode != 0)
            {
                throw new SparkleTaskException(SparkleTaskException.ExceptionTypes.SOLUTIONPACKAGER_ERROR, String.Format("Solution Packager exited with error {0}", exitCode));
            }
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            var dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            var dirs = dir.GetDirectories();
            // If the destination directory doesn't exist, create it.
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the files in the directory and copy them to the new location.
            var files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                var temppath = Path.Combine(destDirName, file.Name);
                file.CopyTo(temppath, false);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    var temppath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, temppath, copySubDirs);
                }
            }
        }
        private void Proc_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data!=null) _trace.WriteLine(e.Data.Replace("{", "{{").Replace("}", "}}"));
        }

    }
}
