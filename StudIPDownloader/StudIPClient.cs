using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.IO;

namespace StudIPDownloader
{
    class StudIPClient
    {
        string _user = null;
        string _password = null;
        bool _express = false;

        WebClient wc;
        CookieContainer cc;
        string BASE = "https://elearning.uni-oldenburg.de/";
        string API_BASE = "https://elearning.uni-oldenburg.de/api.php/";

        public StudIPClient(Cookie cookie, bool express)
        {
            setWebClient(cookie);
            this._express = express;
        }
        public StudIPClient(string user, string password, bool express)
        {
            setWebClient();
            this._user = user;
            this._password = password;
            this._express = express;
            login();
        }
        public StudIPClient(string BASE, Cookie cookie, bool express)
        {
            setBase(BASE);
            setWebClient(cookie);
            this._express = express;
        }
        public StudIPClient(string BASE, string user, string password, bool express)
        {
            setBase(BASE);
            setWebClient(null);
            this._user = user;
            this._password = password;
            this._express = express;
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
            wc.DownloadProgressChanged += new DownloadProgressChangedEventHandler(DownloadProgressCallback);
        }

        public bool login()
        {
            wc.DownloadString(BASE); // Notwendige Cookies setzen (Seminar_Session)
            string userSeite = wc.DownloadString(API_BASE + "discovery");

            if (userSeite.StartsWith("<!DOCTYPE html>"))
            {
                //Login nötig
                if(this._user != null && this._password != null) {

                    string login = wc.DownloadString(BASE); // Notwendige Cookies setzen (Seminar_Session)

                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded; charset=UTF-8";
                    wc.Encoding = Encoding.UTF8;
                    string req = "login_ticket=" + WebUtility.UrlEncode(GetBetween(login, "login_ticket\" value=\"", "\">")) +
                        "&security_token=" + WebUtility.UrlEncode(GetBetween(login, "security_token\" value=\"", "\">")) +
                        "&loginname=" + _user +
                        "&password=" + _password +
                        "&source=" + WebUtility.UrlEncode(GetBetween(login, "source\" value=\"", "\">")) + 
                        "&target=" + WebUtility.UrlEncode(GetBetween(login, "target\" value=\"", "\">"));


                    string HtmlResult = wc.UploadString(BASE + "plugins.php/uollayoutplugin/login?cancel_login=1",
                       req);

                    Console.WriteLine(HtmlResult);

                    userSeite = wc.DownloadString(API_BASE + "discovery");
                    Console.WriteLine();
                    Console.WriteLine(userSeite);
                    if (userSeite.StartsWith("<!DOCTYPE html>"))
                    {
                        Console.WriteLine("Login fehlerhaft");
                        throw ( new WebException("Unauthorized"));
                    }
                }
                else
                {
                    throw (new WebException("Unauthorized"));
                }
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
                Console.WriteLine(course.First.title + " (" + course.First.course_id + ")");
                string semesterName = "Allgemein";
                Semester semester = new Semester(false);
                if (course.First.start_semester != null)
                {
                    string semesterID = course.First.start_semester;
                    semester = getSemesterToken(semesterID.ToString().Replace("/api.php/semester/", ""));
                    semesterName = ReplaceInvalidChars(semester.name);
                }

                if (!_express || (_express && semester.active ))
                {
                    if (!Directory.Exists(localPath + Path.DirectorySeparatorChar + semesterName))
                    {
                        Directory.CreateDirectory(localPath + Path.DirectorySeparatorChar + semesterName);
                    }

                    dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("course/" + course.First.course_id + "/top_folder"));
                    syncSubfolder(localPath + Path.DirectorySeparatorChar + semesterName, (string)folders.SelectToken("id"), Path.DirectorySeparatorChar + RemoveInvalidChars((string)course.First.title));
                }
            }
        }

        void syncSubfolder(string localPath, string parent, string path = "")
        {
            if(!Directory.Exists(localPath + path))
            {
                Directory.CreateDirectory(localPath + path);
            }

            try
            {

                dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("folder/" + parent));

                foreach (var topFolder in folders.subfolders)
                {
                    string folder_id = (string)topFolder.SelectToken("id");
                    string name = (string)topFolder.SelectToken("name");
                    string folder_type = (string)topFolder.SelectToken("folder_type");

                    if (!String.IsNullOrEmpty(name))
                    {
                        name = RemoveInvalidChars(name);
                        Console.WriteLine("->" + name + " (" + folder_id + ")");
                        syncSubfolder(localPath,folder_id, path + Path.DirectorySeparatorChar + name);
                    }
                }
                foreach (var files in folders.file_refs)
                {
                    string file_id = (string)files.SelectToken("id");
                    string name = (string)files.SelectToken("name");
                    int size = (int)files.SelectToken("size");
                    int chdate = (int)files.SelectToken("chdate");
                    bool is_downloadable = (bool)files.SelectToken("is_downloadable");

                    if (is_downloadable && !String.IsNullOrEmpty(name))
                    {
                        name = RemoveInvalidChars(name);
                        downloadFile(localPath, path, new Datei(file_id, name, size, chdate));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Fehler beim Downloaden des Verzeichnises: " + parent + "\r\n" + ex.Message);
            }
        }

        void downloadFile(string localPath, string path, Datei datei)
        {
            string pfad = localPath + path + Path.DirectorySeparatorChar + datei.filename;

            if(!checkFile(pfad, datei))
            {
                Console.WriteLine(path + Path.DirectorySeparatorChar + datei.filename + " (" + datei.id + ")");
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
