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
        /*string _user;
        string _password;*/
        WebClient wc;
        CookieContainer cc;
        string BASE = "https://elearning.uni-oldenburg.de/";
        string API_BASE = "https://elearning.uni-oldenburg.de/api.php/";

        public StudIPClient(Cookie cookie)
        {
            setWebClient(cookie);
        }
        /*public StudIPClient(string user, string password)
        {
            setWebClient();
            this._user = user;
            this._password = password;
            login();
        }*/
        public StudIPClient(string BASE, Cookie cookie)
        {
            this.BASE = BASE;
            this.API_BASE = BASE + "api.php/";
            setWebClient(cookie);
        }
        /*public StudIPClient(string user, string password, string BASE)
        {
            this.BASE = BASE;
            this.API_BASE = BASE + "api.php/";
            setWebClient();
            this._user = user;
            this._password = password;
            login();
        }*/

        public void setWebClient(Cookie cookie)
        {
            cc = new CookieContainer();
            cc.Add(cookie);
            wc = new WebClientEx(cc);
        }

        public bool login()
        {
            string userSeite = wc.DownloadString(API_BASE + "discovery");

            if (userSeite.StartsWith("<!DOCTYPE html>"))
            {
                return false;
                //Login nötig
                /*
                wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                string req = "login_ticket=" + 
                    WebUtility.UrlEncode(GetBetween(userSeite, "login_ticket\" value=\"", "\">")) + "&security_token=" + 
                    WebUtility.UrlEncode(GetBetween(userSeite, "security_token\" value=\"", "\">")) + "&loginname=" + 
                    _user + "&password=" + 
                    _password +
                    "&source=https%3A%2F%2Felearning.uni-oldenburg.de%2Findex.php%3Flogout%3Dtrue%26set_language%3D%26cancel_login%3D1&target=https%3A%2F%2Felearning.uni-oldenburg.de%2Findex.php%3Flogout%3Dtrue%26set_language%3D";

                string HtmlResult = wc.UploadString(BASE + "plugins.php/uollayoutplugin/login?cancel_login=1",
                   req);

                Console.WriteLine(HtmlResult);

                userSeite = wc.DownloadString(API_BASE + "discovery");

                if (userSeite.StartsWith("<!DOCTYPE html>"))
                {
                    Console.WriteLine("Login fehlerhaft");
                    
                }*/
            }
            //Bereits eingeloggt
            return true;
        }

        public string getAPI(string path)
        {
            try
            {
                return wc.DownloadString(API_BASE + path);
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

        string GetBetween(string source, string start, string end)
        {
            var startPos = source.IndexOf(start, StringComparison.Ordinal);
            if (startPos < 0) return null;
            startPos += start.Length;
            var endPos = source.IndexOf(end, startPos, StringComparison.Ordinal);
            return endPos < 0 ? null : source.Substring(startPos, endPos - startPos - 1);
        }

        public string getUserID()
        {
            string api = getAPI("user");
            JToken user = JObject.Parse(api);
            return (string)user.SelectToken("user_id");
        }

        public string getSemesterToken(string semesterID)
        {
            string api = getAPI("semester/" + semesterID);
            JToken semester = JObject.Parse(api);
            return (string)semester.SelectToken("token");
        }

        public void syncFiles(string localPath)
        {
            //@TODO: Add Pagination
            dynamic courses = JsonConvert.DeserializeObject<dynamic>(getAPI("user/" + getUserID() + "/courses"));
            foreach (var course in courses.collection)
            {
                Console.WriteLine(course.First.title + " (" + course.First.course_id + ")");
                string semester = "Allgemein";
                if (course.First.start_semester != null)
                {
                    string semesterID = course.First.start_semester;
                    semester = ReplaceInvalidChars(getSemesterToken(semesterID.ToString().Replace("/api.php/semester/", "")));
                }

                if (!Directory.Exists(localPath + Path.DirectorySeparatorChar + semester))
                {
                    Directory.CreateDirectory(localPath + Path.DirectorySeparatorChar + semester);
                }

                dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("course/" + course.First.course_id + "/top_folder"));
                syncSubfolder(localPath + Path.DirectorySeparatorChar + semester, (string)folders.SelectToken("id"), Path.DirectorySeparatorChar + RemoveInvalidChars((string)course.First.title));
            }
        }

        void syncSubfolder(string localPath, string parent, string path = "")
        {
            if(!Directory.Exists(localPath + path))
            {
                Directory.CreateDirectory(localPath + path);
            }
 
            dynamic folders = JsonConvert.DeserializeObject<dynamic>(getAPI("folder/" + parent));

            foreach (var topFolder in folders.subfolders)
            {
                string folder_id = (string)topFolder.SelectToken("id");
                string name = (string)topFolder.SelectToken("name");
                string folder_type = (string)topFolder.SelectToken("folder_type");

                if (!String.IsNullOrEmpty(name))
                {
                    name = RemoveInvalidChars(name);
                    Console.WriteLine("->" + folder_id + " (" + name + ")");
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
                    Console.WriteLine(path + name + " (" + file_id + ")");
                    downloadFile(localPath, path, new Datei(file_id, name, size, chdate));
                }
            }
        }

        void downloadFile(string localPath, string path, Datei datei)
        {
            string pfad = localPath + path + Path.DirectorySeparatorChar + datei.filename;

            if(!checkFile(pfad, datei))
            {
                wc.DownloadFile(API_BASE + "file/" + datei.id + "/download", pfad);
            }
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
