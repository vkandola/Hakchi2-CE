﻿using com.clusterrr.hakchi_gui.Properties;
using com.clusterrr.util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace com.clusterrr.hakchi_gui.Tasks
{
    class SyncTask
    {
        const long reservedMemory = 30 * 1024 * 1024;

        public NesMenuCollection Games
        {
            get; set;
        }

        public static bool ShowFoldersManager(TaskerForm tasker, NesMenuCollection collection)
        {
            if (tasker.Disposing) return false;
            if (tasker.InvokeRequired)
            {
                return (bool)tasker.Invoke(new Func<TaskerForm, NesMenuCollection, bool>(ShowFoldersManager), new object[] { tasker, collection });
            }
            try
            {
                using (FoldersManagerForm form = new FoldersManagerForm(collection, MainForm.StaticRef))
                {
                    var prevState = tasker.SetState(TaskerForm.State.Paused);
                    if (form.ShowDialog() != DialogResult.OK)
                        return false;
                    tasker.SetState(prevState);
                }
                return true;
            }
            catch (InvalidOperationException) { }
            return false;
        }

        public bool ShowExportDialog(TaskerForm tasker)
        {
            if (tasker.Disposing) return false;
            if (tasker.InvokeRequired)
            {
                return (bool)tasker.Invoke(new Func<TaskerForm, bool>(ShowExportDialog), new object[] { tasker });
            }
            try
            {
                using (ExportGamesDialog driveSelectDialog = new ExportGamesDialog())
                {
                    var prevState = tasker.SetState(TaskerForm.State.Paused);
                    if (driveSelectDialog.ShowDialog() != DialogResult.OK)
                        return false;
                    this.exportLinked = driveSelectDialog.LinkedExport;
                    this.exportDirectory = driveSelectDialog.ExportPath;
                    if (!Directory.Exists(driveSelectDialog.ExportPath))
                        Directory.CreateDirectory(driveSelectDialog.ExportPath);
                    tasker.SetState(prevState);
                }
                return true;
            }
            catch (InvalidOperationException) { }
            return false;
        }

        private string exportDirectory;
        private bool exportLinked;

        public SyncTask()
        {
            Games = new NesMenuCollection();
        }

        public TaskerForm.Conclusion ExportGames(TaskerForm tasker, Object syncObject = null)
        {
            int maxProgress = 100;
            tasker.SetTitle(Resources.ExportGames);
            tasker.SetProgress(0, maxProgress, TaskerForm.State.Starting, Resources.SelectDrive);
            if (Games == null || Games.Count == 0)
                throw new Exception("No games to upload");

            // select drive
            exportLinked = false;
            exportDirectory = string.Empty;
            if (!ShowExportDialog(tasker))
                return TaskerForm.Conclusion.Abort;

            // building folders
            tasker.SetProgress(5, maxProgress, TaskerForm.State.Starting, Resources.BuildingMenu);
            if (ConfigIni.Instance.FoldersMode == NesMenuCollection.SplitStyle.Custom)
            {
                if (!ShowFoldersManager(tasker, Games))
                    return TaskerForm.Conclusion.Abort;
                Games.AddBack();
            }
            else
                Games.Split(ConfigIni.Instance.FoldersMode, ConfigIni.Instance.MaxGamesPerFolder);

            // generate menus and game files ready to be uploaded
            tasker.SetStatus(Resources.AddingGames);
            Dictionary<string, string> originalGames = new Dictionary<string, string>();
            var localGameSet = new HashSet<ApplicationFileInfo>();
            var stats = new GamesTreeStats();
            AddMenu(
                Games,
                originalGames,
                exportLinked ? NesApplication.CopyMode.LinkedExport : NesApplication.CopyMode.Export,
                localGameSet,
                stats);
            tasker.SetProgress(15, maxProgress);

            // check free space
            tasker.SetStatus(Resources.CalculatingDiff);
            var drive = new DriveInfo(Path.GetPathRoot(exportDirectory));
            long storageTotal = drive.TotalSize;
            long storageUsed = Shared.DirectorySize(exportDirectory);
            long storageFree = drive.AvailableFreeSpace;
            long maxGamesSize = storageUsed + storageFree;
            Debug.WriteLine($"Exporting to folder: {exportDirectory}");
            Debug.WriteLine($"Drive: {drive.Name} ({drive.DriveFormat})");
            Debug.WriteLine(string.Format("Storage size: {0:F1}MB, used by games: {1:F1}MB, free: {2:F1}MB", storageTotal / 1024.0 / 1024.0, storageUsed / 1024.0 / 1024.0, storageFree / 1024.0 / 1024.0));
            Debug.WriteLine(string.Format("Available for games: {0:F1}MB", maxGamesSize / 1024.0 / 1024.0));
            if (stats.TotalSize > maxGamesSize)
            {
                throw new Exception(
                    string.Format(Resources.MemoryFull, Shared.SizeSuffix(stats.TotalSize)) + "\r\n" +
                    string.Format(Resources.MemoryStatsExport, Shared.SizeSuffix(maxGamesSize)));
            }

            // list current files on drive
            var exportDriveGameSet = ApplicationFileInfo.GetApplicationFileInfoForDirectory(exportDirectory);

            // calculating diff
            var exportDriveGamesToDelete = exportDriveGameSet.Except(localGameSet);
            var localGamesToTransfer = localGameSet.Except(exportDriveGameSet);

#if VERY_DEBUG
            Debug.WriteLine("LOCAL GAMES SET:");
            ApplicationFileInfo.DebugListHashSet(localGameSet);
            Debug.WriteLine("FILES TO DELETE:");
            ApplicationFileInfo.DebugListHashSet(exportDriveGamesToDelete);
            Debug.WriteLine("FILES TO UPLOAD:");
            ApplicationFileInfo.DebugListHashSet(localGamesToTransfer);
#endif

            // delete any files on the device that aren't present in current layout
            tasker.SetStatus(Resources.CleaningUp);
            DeleteLocalApplicationFilesFromDirectory(exportDriveGamesToDelete, exportDirectory);

            // now transfer whatever games are remaining
            Debug.WriteLine("Exporting games: " + Shared.SizeSuffix(stats.TotalSize));
            int i = 25;
            maxProgress = 25 + localGamesToTransfer.Count();
            tasker.SetProgress(25, maxProgress, TaskerForm.State.Running, Resources.CopyingGames);
            foreach (var afi in localGamesToTransfer)
            {
                string path = new Uri(exportDirectory + "/" + afi.FilePath).LocalPath;
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!string.IsNullOrEmpty(afi.LocalFilePath))
                {
                    File.Copy(afi.LocalFilePath, path, true);
                }
                else
                {
                    if (afi.FileStream == null || !afi.FileStream.CanRead)
                    {
                        Debug.WriteLine($"\"{afi.FilePath}\": no source data or stream or unreadable");
                    }
                    else
                    {
                        afi.FileStream.Position = 0;
                        using (var f = File.Open(path, FileMode.Create))
                            afi.FileStream.CopyTo(f);
                        File.SetLastWriteTimeUtc(path, afi.ModifiedTime);
                    }
                }
                tasker.SetProgress(++i, maxProgress);
            }

#if DEBUG
            using (var gamesTar = new TarStream(localGamesToTransfer, "."))
            {
                if (gamesTar.Length > 0)
                {
#if VERY_DEBUG
                    gamesTar.DebugWrite();
#endif
                    Debug.WriteLine("Creating debug tar archive: " + Shared.SizeSuffix(gamesTar.Length));
                    File.Delete(Path.Combine(Program.BaseDirectoryExternal, "DebugSyncOutput.tar"));
                    gamesTar.CopyTo(File.OpenWrite(Path.Combine(Program.BaseDirectoryExternal, "DebugSyncOutput.tar")));
                }
            }
#endif

            // show resulting games directory
            tasker.SetStatus(Resources.PleaseWait);
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = exportDirectory
                }
            };
            process.Start();

            return TaskerForm.Conclusion.Success;
        }

        public TaskerForm.Conclusion UploadGames(TaskerForm tasker, Object syncObject = null)
        {
            int maxProgress = 135;
            tasker.SetTitle(Resources.UploadGames);
            if (Games == null || Games.Count == 0)
                throw new Exception("No games to upload");

            // building folders
            tasker.SetProgress(0, maxProgress, TaskerForm.State.Starting, Resources.BuildingMenu);
            if (ConfigIni.Instance.FoldersMode == NesMenuCollection.SplitStyle.Custom)
            {
                if (!ShowFoldersManager(tasker, Games))
                    return TaskerForm.Conclusion.Abort;
                Games.AddBack();
            }
            else
                Games.Split(ConfigIni.Instance.FoldersMode, ConfigIni.Instance.MaxGamesPerFolder);

            // prepare transfer
            var shell = hakchi.Shell;
            try
            {
                if (!KernelTask.WaitForShell(tasker))
                {
                    return TaskerForm.Conclusion.Abort;
                }
                hakchi.ShowSplashScreen();

                // paths
                string gameSyncPath = hakchi.GetRemoteGameSyncPath();
                string gamesPath = shell.ExecuteSimple("hakchi get gamepath", 2000, true).Trim();
                string rootFsPath = shell.ExecuteSimple("hakchi get rootfs", 2000, true).Trim();
                string squashFsPath = shell.ExecuteSimple("hakchi get squashfs", 2000, true).Trim();

                // clean up previous directories (separate game storage vs not)
                tasker.SetStatus(Resources.CleaningUp);
                shell.ExecuteSimple("find \"$(hakchi findGameSyncStorage)/\" -maxdepth 1 | grep -" + (ConfigIni.Instance.SeparateGameStorage ? "v" : "") + "Ee '(/snes(-usa|-eur|-jpn)?|/nes(-usa|-jpn)?|/)$' | while read f; do rm -rf \"$f\"; done", 0, true);
                tasker.SetProgress(5, maxProgress);

                // generate menus and game files ready to be uploaded
                tasker.SetStatus(Resources.AddingGames);
                Dictionary<string, string> originalGames = new Dictionary<string, string>();
                var localGameSet = new HashSet<ApplicationFileInfo>();
                var stats = new GamesTreeStats();
                AddMenu(
                    Games,
                    originalGames,
                    ConfigIni.Instance.SyncLinked ? NesApplication.CopyMode.LinkedSync : NesApplication.CopyMode.Sync,
                    localGameSet,
                    stats);
                tasker.SetProgress(15, maxProgress);

                // calculating size constraints
                tasker.SetStatus(Resources.CalculatingDiff);
                long gamesSize;
                long saveStatesSize;
                long storageTotal;
                long storageUsed;
                long storageFree;
                hakchi.GetStorageStats(out gamesSize, out saveStatesSize, out storageTotal, out storageUsed, out storageFree);
                long maxGamesSize = (storageFree + gamesSize) - reservedMemory;
                if (stats.TotalSize > maxGamesSize)
                {
                    throw new Exception(string.Format(Resources.MemoryFull, stats.TotalSize) + "\r\n" +
                        string.Format(Resources.MemoryStats.Replace("|", "\r\n"),
                        storageTotal / 1024 / 1024,
                        maxGamesSize / 1024 / 1024,
                        saveStatesSize / 1024 / 1024,
                        (storageUsed - gamesSize - saveStatesSize) / 1024 / 1024));
                }

                // get the remote list of files, timestamps, and sizes
                string gamesOnDevice = shell.ExecuteSimple($"mkdir -p \"{gameSyncPath}\"; cd \"{gameSyncPath}\"; find . -type f -exec sh -c \"stat \\\"{{}}\\\" -c \\\"%n %s %y\\\"\" \\;", 0, true);
                var remoteGameSet = ApplicationFileInfo.GetApplicationFileInfoFromConsoleOutput(gamesOnDevice);

                // delete any remote files that aren't present locally
                tasker.SetStatus(Resources.CleaningUp);
                var remoteGamesToDelete = remoteGameSet.Except(localGameSet);
                DeleteRemoteApplicationFiles(remoteGamesToDelete);

                // only keep the local files that aren't matching on the mini
                var localGamesToUpload = localGameSet.Except(remoteGameSet);

#if VERY_DEBUG
                // debug info
                Debug.WriteLine("FILES TO DELETE:");
                ApplicationFileInfo.DebugListHashSet(remoteGamesToDelete);
                Debug.WriteLine("FILES TO UPLOAD:");
                ApplicationFileInfo.DebugListHashSet(localGamesToUpload);
#endif

                // now transfer whatever games are remaining
                tasker.SetProgress(20, maxProgress, TaskerForm.State.Running, Resources.UploadingGames);
                shell.ExecuteSimple("hakchi eval 'umount \"$gamepath\"'");
                bool uploadSuccessful = false;
                if (ConfigIni.Instance.ForceSSHTransfers || hakchi.Shell is clovershell.ClovershellConnection) // use tar stream when detecting clovershell
                {
                    Debug.WriteLine("Uploading through tar file");
                    using (var gamesTar = new TarStream(localGamesToUpload, "."))
                    {
                        Debug.WriteLine($"Upload size: " + Shared.SizeSuffix(gamesTar.Length));
#if VERY_DEBUG
                        gamesTar.DebugWrite();
#endif
                        if (gamesTar.Length > 0)
                        {
                            DateTime startTime = DateTime.Now;
                            DateTime lastTime = startTime;
                            bool done = false;
                            gamesTar.OnReadProgress += delegate (long pos, long len)
                            {
                                if (!done)
                                {
                                    if (DateTime.Now.Subtract(lastTime).TotalMilliseconds >= 100)
                                    {
                                        lastTime = DateTime.Now;
                                        double totalTime = lastTime.Subtract(startTime).TotalSeconds;
                                        string estSpeed = ((int)(pos / totalTime / 1024)).ToString();
                                        tasker.SetProgress(20 + (int)((double)pos / len * 100), maxProgress);
                                        tasker.SetStatus(string.Format(Resources.Uploading, estSpeed + " kBps"));
                                        Debug.WriteLine($"{pos} / {len} : {estSpeed}");
                                    }
                                }
                            };
                            shell.Execute($"tar -xvC \"{gameSyncPath}\"", gamesTar, null, null, 0, true);
                            Debug.WriteLine("Uploaded " + (int)(gamesTar.Length / 1024) + "kb in " + DateTime.Now.Subtract(startTime).TotalSeconds + " seconds");

                            tasker.SetState(TaskerForm.State.Finishing);
                            uploadSuccessful = true;
                            done = true;

#if DEBUG
                            File.Delete(Program.BaseDirectoryExternal + "\\DebugSyncOutput.tar");
                            gamesTar.Position = 0;
                            gamesTar.CopyTo(File.OpenWrite(Program.BaseDirectoryExternal + "\\DebugSyncOutput.tar"));
#endif
                        }
                    }
                }
                else if (hakchi.Shell is ssh.SshClientWrapper) // using ftp when detecting ssh
                {
                    Debug.WriteLine("Uploading through FTP");
                    tasker.SetProgress(20, maxProgress, TaskerForm.State.Running, Resources.UploadingGames);
                    using (var ftp = new FtpWrapper(localGamesToUpload))
                    {
                        Debug.WriteLine($"Upload size: " + Shared.SizeSuffix(ftp.Length));
                        if (ftp.Length > 0)
                        {
                            DateTime startTime = DateTime.Now;
                            DateTime lastTime = startTime;
                            ftp.OnReadProgress += delegate (long pos, long len, string filename)
                            {
                                if (DateTime.Now.Subtract(lastTime).TotalMilliseconds >= 100)
                                {
                                    lastTime = DateTime.Now;
                                    double totalTime = lastTime.Subtract(startTime).TotalSeconds;
                                    string estSpeed = ((int)(pos / totalTime / 1024)).ToString();
                                    tasker.SetProgress(20 + (int)((double)pos / len * 100), maxProgress);
                                    tasker.SetStatus(string.Format(Resources.Uploading, Path.GetFileName(filename) + " (" + estSpeed + " kBps)"));
                                    Debug.WriteLine($"{pos} / {len} : {estSpeed}");
                                }
                            };
                            if (ftp.Connect(hakchi.STATIC_IP, 21, hakchi.USERNAME, hakchi.PASSWORD))
                            {
                                ftp.Upload(gameSyncPath);
                                uploadSuccessful = true;
                                Debug.WriteLine("Uploaded " + (int)(ftp.Length / 1024) + "kb in " + DateTime.Now.Subtract(startTime).TotalSeconds + " seconds");
                            }
                            tasker.SetState(TaskerForm.State.Finishing);
                        }
                    }

                }
                else
                {
                    Debug.WriteLine("Unknown shell detected, aborting transfer");
                }

                // don't continue if upload wasn't successful
                if (!uploadSuccessful)
                    return TaskerForm.Conclusion.Error;

                // Finally, delete any empty directories we may have left during the differential sync
                tasker.SetStatus(Resources.CleaningUp);
                shell.ExecuteSimple($"for f in $(find \"{gameSyncPath}\" -type d -mindepth 1 -maxdepth 2); do {{ ls -1 \"$f\" | grep -v pixelart | grep -v autoplay " +
                    "| wc -l | { read wc; test $wc -eq 0 && rm -rf \"$f\"; } } ; done", 0);
                tasker.SetProgress(125, maxProgress);

                tasker.SetStatus(Resources.UploadingOriginalGames);
                int i = 0;
                foreach (var originalCode in originalGames.Keys)
                {
                    string originalSyncCode = "";
                    switch (ConfigIni.Instance.ConsoleType)
                    {
                        case MainForm.ConsoleType.NES:
                        case MainForm.ConsoleType.Famicom:
                            originalSyncCode =
                                $"src=\"{squashFsPath}{gamesPath}/{originalCode}\" && " +
                                $"dst=\"{gameSyncPath}/{originalGames[originalCode]}/{originalCode}\" && " +
                                $"mkdir -p \"$dst\" && " +
                                $"([ -e \"$dst/autoplay\" ] || ln -s \"$src/autoplay\" \"$dst/\") && " +
                                $"([ -e \"$dst/pixelart\" ] || ln -s \"$src/pixelart\" \"$dst/\")";
                            break;
                        case MainForm.ConsoleType.SNES:
                        case MainForm.ConsoleType.SuperFamicom:
                            originalSyncCode =
                                $"src=\"{squashFsPath}{gamesPath}/{originalCode}\" && " +
                                $"dst=\"{gameSyncPath}/{originalGames[originalCode]}/{originalCode}\" && " +
                                $"mkdir -p \"$dst\" && " +
                                $"([ -e \"$dst/autoplay\" ] || ln -s \"$src/autoplay\" \"$dst/\")";
                            break;
                    }
                    shell.ExecuteSimple(originalSyncCode, 1000, true);
                    tasker.SetProgress(125 + (int)((double)++i / originalGames.Count * 10), maxProgress);
                };

                tasker.SetStatus(Resources.UploadingConfig);
                hakchi.SyncConfig(ConfigIni.GetConfigDictionary());
            }
            finally
            {
                try
                {
                    if (shell.IsOnline)
                        shell.ExecuteSimple("hakchi overmount_games; uistart", 100);
                }
                catch { }
            }

            return TaskerForm.Conclusion.Success;
        }

        private class GamesTreeStats
        {
            public List<NesMenuCollection> allMenus = new List<NesMenuCollection>();
            public int TotalGames = 0;
            public long TotalSize = 0;
            public long TransferSize = 0;
        }

        private void AddMenu(NesMenuCollection menuCollection, Dictionary<string, string> originalGames, NesApplication.CopyMode copyMode, HashSet<ApplicationFileInfo> localGameSet = null, GamesTreeStats stats = null)
        {
            if (stats == null)
                stats = new GamesTreeStats();
            if (!stats.allMenus.Contains(menuCollection))
                stats.allMenus.Add(menuCollection);
            int menuIndex = stats.allMenus.IndexOf(menuCollection);
            string targetDirectory = string.Format("{0:D3}", menuIndex);

            foreach (var element in menuCollection)
            {
                if (element is NesApplication)
                {
                    var game = element as NesApplication;
                    long gameSize = game.Size();

                    Debug.WriteLine(string.Format("Processing {0} ('{1}'), size: {2}KB", game.Code, game.Name, gameSize / 1024));
                    gameSize = game.CopyTo(targetDirectory, localGameSet, copyMode);
                    stats.TotalSize += gameSize;
                    stats.TransferSize += gameSize;
                    stats.TotalGames++;
                    /*
                    try
                    {
                        if (gameCopy is ISupportsGameGenie && File.Exists(gameCopy.GameGeniePath))
                        {
                            bool compressed = false;
                            if (gameCopy.DecompressPossible().Count() > 0)
                            {
                                gameCopy.Decompress();
                                compressed = true;
                            }
                            (gameCopy as ISupportsGameGenie).ApplyGameGenie();
                            if (compressed)
                                gameCopy.Compress();
                            File.Delete((gameCopy as NesApplication).GameGeniePath);
                        }
                    }
                    catch (GameGenieFormatException ex)
                    {
                        Debug.WriteLine(string.Format(Resources.GameGenieFormatError, ex.Code, game.Name));
                    }
                    catch (GameGenieNotFoundException ex)
                    {
                        Debug.WriteLine(string.Format(Resources.GameGenieNotFound, ex.Code, game.Name));
                    }
                    */

                    // legacy
                    if (game.IsOriginalGame)
                        originalGames[game.Code] = $"{menuIndex:D3}";
                }
                if (element is NesMenuFolder)
                {
                    var folder = element as NesMenuFolder;
                    if (folder.Name == Resources.FolderNameTrashBin)
                        continue; // skip recycle bin!

                    if (!stats.allMenus.Contains(folder.ChildMenuCollection))
                    {
                        stats.allMenus.Add(folder.ChildMenuCollection);
                        AddMenu(folder.ChildMenuCollection, originalGames, copyMode, localGameSet, stats);
                    }
                    folder.ChildIndex = stats.allMenus.IndexOf(folder.ChildMenuCollection);

                    long folderSize = folder.CopyTo(targetDirectory, localGameSet);
                    stats.TotalSize += folderSize;
                    stats.TransferSize += folderSize;
                    Debug.WriteLine(string.Format("Processed folder {0} ('{1}'), size: {2}KB", folder.Code, folder.Name, folderSize / 1024));
                }
            }
        }

        private static void DeleteRemoteApplicationFiles(IEnumerable<ApplicationFileInfo> filesToDelete)
        {
            using (MemoryStream commandBuilder = new MemoryStream())
            {
                string data = $"#!/bin/sh\ncd \"{hakchi.GetRemoteGameSyncPath()}\"\n";
                commandBuilder.Write(Encoding.UTF8.GetBytes(data), 0, data.Length);

                foreach (ApplicationFileInfo appInfo in filesToDelete)
                {
                    data = $"rm \"{appInfo.FilePath}\"\n";
                    commandBuilder.Write(Encoding.UTF8.GetBytes(data), 0, data.Length);
                }

                try
                {
                    hakchi.Shell.Execute("cat > /tmp/cleanup.sh", commandBuilder, null, null, 5000, true);
                    hakchi.Shell.ExecuteSimple("chmod +x /tmp/cleanup.sh && /tmp/cleanup.sh", 0, true);
                }
                finally
                {
                    hakchi.Shell.ExecuteSimple("rm /tmp/cleanup.sh");
                }
            }
        }

        private static void DeleteLocalApplicationFilesFromDirectory(IEnumerable<ApplicationFileInfo> filesToDelete, string rootDirectory)
        {
            foreach (ApplicationFileInfo appInfo in filesToDelete)
            {
                string filepath = rootDirectory + appInfo.FilePath.Substring(1).Replace('/', '\\');
                File.Delete(filepath);

                // determine if the folder is empty now -- if so, delete the folder also
                string directory = Path.GetDirectoryName(filepath);
                var dirInfo = new DirectoryInfo(directory);
                if (dirInfo.GetFiles().Length == 0 && dirInfo.GetDirectories().Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
        }

    }
}
