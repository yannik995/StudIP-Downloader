using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace StudIPDownloader
{
    class StudIPClient
    {
        string _user = null;
        string _password = null;
        bool _express = false;
        bool _downloadOverwrite = false;
        string[] _ignore;

        WebClient wc;
        CookieContainer cc;
        string BASE = "https://elearning.uni-oldenburg.de/";
        string API_BASE = "https://elearning.uni-oldenburg.de/api.php/";

        public StudIPClient(Cookie cookie, bool express, string ignore)
        {
            setWebClient(cookie);
            this._express = express;
            this._ignore = ignore.Split(",");
        }
        public StudIPClient(string user, string password, bool express, string ignore)
        {
            setWebClient();
            this._user = user;
            this._password = password;
            this._express = express;
            this._ignore = ignore.Split(",");
            login();
        }
        public StudIPClient(string BASE, Cookie cookie, bool express, string ignore, bool downloadOverwrite)
        {
            setBase(BASE);
            setWebClient(cookie);
            this._express = express;
            this._ignore = ignore.Split(",");
            this._downloadOverwrite = downloadOverwrite;
        }
        public StudIPClient(string BASE, string user, string password, bool express, string ignore)
        {
            setBase(BASE);
            setWebClient(null);
            this._user = user;
            this._password = password;
            this._express = express;
            this._ignore = ignore.Split(",");
            login();
        }
        public StudIPClient(string BASE, string user, string password, bool express, string ignore, bool downloadOverwrite)
        {
            setBase(BASE);
            setWebClient(null);
            this._user = user;
            this._password = password;
            this._express = express;
            this._ignore = ignore.Split(",");
            this._downloadOverwrite = downloadOverwrite;
            login();
        }

        public void setBase(string BASE)
        {
            this.BASE = BASE;
            this.API_BASE = BASE + "api.php/";
        }

        public void setWebClient(Cookie cookie = null)
        {
            cc = new CookieContainer();
            if(cookie != null)
            {
                cc.Add(cookie);
            }
            wc = new WebClientEx(cc);
            wc.Headers[HttpRequestHeader.UserAgent] = "StudIP-Downloader"; //Ohne User Agent funktioniert der Login nicht
            wc.DownloadProgressChanged += new DownloadProgressChangedEventHandler(DownloadProgressCallback);
        }

        public bool login()
        {
            //wc.DownloadString(BASE); // Notwendige Cookies setzen (Seminar_Session)
            string auth = Convert.ToBase64String(Encoding.ASCII.GetBytes(this._user + ":" + this._password));
            
            //wc.Headers[HttpRequestHeader.Accept] = "*/*";
            wc.Headers[HttpRequestHeader.Authorization] = string.Format("Basic {0}", auth);

            string userSeite = wc.DownloadString(API_BASE + "discovery");

            if (userSeite.StartsWith("<!DOCTYPE html>"))
            {
                throw (new WebException("Unauthorized"));
            }
            //Bereits eingeloggt
            return true;
        }

        public string getAPI(string path)
        {
            try
            {
                string dl = wc.DownloadString(API_BASE + path);
                if (dl.StartsWith("<")) //Wenn kein JSON
                {
                    if (login()) { 
                        dl = wc.DownloadString(API_BASE + path);
                    }
                }
                return dl;
            }
            catch (WebException webException)
            {
                if(webException.Message.Contains("(401) Unauthorized") && login())
                {
                    return wc.DownloadString(API_BASE + path);
                }
                throw (webException);
            }
        }

        public string getUserID()
        {
            string api = getAPI("user");
            JToken user = JObject.Parse(api);
            return (string)user.SelectToken("user_id");
        }

        Dictionary<string, Semester> SemesterDict = new Dictionary<string, Semester>();
        public Semester getSemesterToken(string semesterID)
        {
            if (!SemesterDict.ContainsKey(semesterID)) //Mit Dictionary API Requests sparen  
            {
                string api = getAPI("semester/" + semesterID);
                JToken semester = JObject.Parse(api);

                Int32 unixTimestamp = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

                SemesterDict[semesterID] = new Semester((string)semester.SelectToken("id"), (string)semester.SelectToken("token"), 
                    ((int)semester.SelectToken("begin") < unixTimestamp && (int)semester.SelectToken("end") > unixTimestamp)); // Prüfung, ob Eintrag im aktuellen Semester ist.
            }
            return SemesterDict[semesterID];

        }

        public void syncFiles(string localPath)
        {
            //@TODO: Add Pagination mit limit=1000 nicht mehr nötig.
            dynamic courses = JsonConvert.DeserializeObject<dynamic>(getAPI("user/" + getUserID() + "/courses?limit=1000"));
            foreach (var course in courses.collection)
            {
                Console.Write(course.First.title); // + " (" + course.First.course_id + ")"
                string semesterName = "Allgemein";
                Semester semester = new Semester(false);
                if (course.First.start_semester != null)
                {
                    string semesterID = course.First.start_semester;
                    semester = getSemesterToken(semesterID.ToString().Replace("/api.php/semester/", ""));
                    if(semester.name != null) { 
                        semesterName = ReplaceInvalidChars(semester.name);
                    }
                }

                if (!_express || (_express && semester.active ))
                {
                    Console.WriteLine();

                    if (!Directory.Exists(localPath + Path.DirectorySeparatorChar + semesterName))
                    {
                        Directory.CreateDirectory(localPath + Path.DirectorySeparatorChar + semesterName);
                    }

                    dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("course/" + course.First.course_id + "/top_folder"));
                    syncSubfolder(localPath + Path.DirectorySeparatorChar + semesterName, (string)folders.SelectToken("id"), Path.DirectorySeparatorChar + RemoveInvalidChars((string)course.First.title), 0);
                }
                else
                {
                    Console.WriteLine(" -> Skip");
                }

            }
        }

        void syncSubfolder(string localPath, string parent, string path = "", int ebene = 1)
        {
            if(!Directory.Exists(localPath + path))
            {
                Directory.CreateDirectory(localPath + path);
            }
            ebene++;
            try
            {

                dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("folder/" + parent));

                if (folders.subfolders != null)
                {
                    foreach (var topFolder in folders.subfolders)
                    {
                        string folder_id = (string)topFolder.SelectToken("id");
                        string name = (string)topFolder.SelectToken("name");
                        string folder_type = (string)topFolder.SelectToken("folder_type");

                        if (!String.IsNullOrEmpty(name) && !_ignore.Contains(name))
                        {
                            name = RemoveInvalidChars(name);
                            Console.WriteLine(new string(' ', ebene) + "->" + name); //+ " (" + folder_id + ")"
                            syncSubfolder(localPath, folder_id, path + Path.DirectorySeparatorChar + name, ebene);
                        }
                        else if (!String.IsNullOrEmpty(name))
                        {
                            Console.WriteLine(new string(' ', ebene) + "->" + name + "-> Skip"); //"(" + folder_id + ")" +
                        }
                    }
                }

                if(folders.file_refs != null) { 
                    foreach (var files in folders.file_refs)
                    {
                        string file_id = (string)files.SelectToken("id");
                        string name = (string)files.SelectToken("name");
                        int size = (int)files.SelectToken("size");
                        int chdate = (int)files.SelectToken("chdate");

                        bool is_downloadable = _downloadOverwrite;
                        try
                        {
                            is_downloadable = (bool)files.SelectToken("is_downloadable");
                        }
                        catch (Exception ex)
                        {
                            if (!_downloadOverwrite) { 
                                Console.WriteLine("Warnung: " + name + "(" + file_id + ") nicht downloadbar (ggf. downloadOverwrite|True in config Datei verwenden)\r\n");
                            }
                        }

                        if ((is_downloadable || _downloadOverwrite) && !String.IsNullOrEmpty(name))
                        {
                            name = RemoveInvalidChars(name);
                            downloadFile(localPath, path, new Datei(file_id, name, size, chdate), ebene);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fehler beim Downloaden des Verzeichnises: " + parent + "\r\n" + ex.Message);
            }
        }

        void downloadFile(string localPath, string path, Datei datei, int ebene = 1)
        {
            string pfad = localPath + path + Path.DirectorySeparatorChar + datei.filename;

            if(!checkFile(pfad, datei))
            {
                Console.WriteLine(new string(' ', ebene) + path + Path.DirectorySeparatorChar + datei.filename ); // + " (" + datei.id + ")"
                try
                {
                    
                    wc.DownloadFile(API_BASE + "file/" + datei.id + "/download", pfad);
                }
                catch (Exception ex)
                {
                    try
                    {
                        //API wirft Fehler bei Dateien > 500MB, nutze dann WEB UI 
                        wc.DownloadFile(BASE + "sendfile.php?force_download=1&type=0&file_id=" + datei.id , pfad);
                    }
                    catch (Exception ex1)
                    {
                        Console.WriteLine("Fehler beim Downloaden der Datei " + datei.filename + "(" + datei.id + ")\r\n" + ex.Message);
                    }
                }
            }
        }

        private static void DownloadProgressCallback(object sender, DownloadProgressChangedEventArgs e)
        {
            // Displays the operation identifier, and the transfer progress.
            Console.WriteLine("{0}    downloaded {1} of {2} bytes. {3} % complete...",
                (string)e.UserState,
                e.BytesReceived,
                e.TotalBytesToReceive,
                e.ProgressPercentage);
        }

        bool checkFile(string pfad, Datei datei)
        {
            if (!File.Exists(pfad))
            {
                return false;
            }
            if(ToUnixTime(File.GetLastWriteTime(pfad)) < datei.chdate)
            {
                Console.WriteLine(datei.filename + " wurde bearbeitet!");
                return false;
            }
            return true;
        }

//Helper 
        string GetBetween(string strSource, string strStart, string strEnd)
        {
            int Start, End;
            if (strSource.Contains(strStart) && strSource.Contains(strEnd))
            {
                Start = strSource.IndexOf(strStart, 0) + strStart.Length;
                End = strSource.IndexOf(strEnd, Start);
                return strSource.Substring(Start, End - Start);
            }
            else
            {
                return "";
            }
        }

        string RemoveInvalidChars(string filename)
        {
            return string.Concat(filename.Trim().Split(Path.GetInvalidFileNameChars()));
        }

        string ReplaceInvalidChars(string filename, string replace = "_")
        {
            return string.Join(replace, filename.Split(Path.GetInvalidFileNameChars()));
        }

        public long ToUnixTime(DateTime dateTime)
        {
            return (long)(dateTime - new DateTime(1970, 1, 1)).TotalSeconds;
        }

    }
}
