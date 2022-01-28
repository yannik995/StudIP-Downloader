using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;

namespace StudIPDownloader
{
    class Program
    {
        private const string PFAD_DEFAULT = @"C:\StudIP-Sync\";
        static string pfad = PFAD_DEFAULT;
        static string SessionCookie = "";
        static string username = "";
        static string password = "";
        static string StudIpURL = "";
        static string ignore = "";
        static bool downloadOverwrite = false;
        static bool express = false;
        static bool savePass = false;
        static void Main(string[] args)
        {

            try
            {
                using (StreamReader sr = new StreamReader("config.ini"))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("Cookie|"))
                        {
                            SessionCookie = line.Split('|')[1];
                        }
                        if (line.StartsWith("User|"))
                        {
                            username = line.Split('|')[1];
                        }
                        if (line.StartsWith("Pass|"))
                        {
                            password = line.Split('|')[1];
                        }
                        if (line.StartsWith("Pfad|"))
                        {
                            pfad = line.Split('|')[1];
                        }
                        if (line.StartsWith("StudIP|"))
                        {
                            StudIpURL = line.Split('|')[1];
                        }
                        if (line.StartsWith("express|"))
                        {
                            express = bool.Parse(line.Split('|')[1]);
                        }
                        if (line.StartsWith("ignore|"))
                        {
                            ignore = line.Split('|')[1];
                        }
                        if (line.StartsWith("downloadOverwrite|"))
                        {
                            downloadOverwrite = bool.Parse(line.Split('|')[1]);
                        }
                        if (line.StartsWith("savePass|"))
                        {
                            savePass = bool.Parse(line.Split('|')[1]);
                        }
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("config.ini konnte nicht gelesen werden: " + ex.Message);
            }

            StudIPClient client = null;
            string[] arguments = Environment.GetCommandLineArgs();

            if (arguments.Length == 1) //Cookie Login Interactive
            {
                if (username != "Beispieluser" && username != "" && password != "")
                {
                    client = new StudIPClient(getStudIP(), username, password, express, ignore, downloadOverwrite);
                }
                else
                {
                    client = new StudIPClient(getStudIP(), getCookie(), express, ignore, downloadOverwrite);
                }
            }
            else if (arguments.Length == 2) //Cookie Login with Parameter
            {
                client = new StudIPClient(getStudIP(), new Cookie("Seminar_Session", arguments[1], "/", new Uri(StudIpURL).Host), express, ignore, downloadOverwrite);
            }
            else if (arguments.Length >= 3) //User Pass Login
            {
                client = new StudIPClient(getStudIP(), arguments[1], arguments[2], express, ignore, downloadOverwrite);
                if (arguments.Length > 3)
                {
                    bool.TryParse(arguments[3], out savePass);
                }
            }

            //StudIPClient client = new StudIPClient(getStudIP(), ReadLine("Username: "), ReadLine("Passwort: "), express);

            if (pfad == PFAD_DEFAULT)
            {
                pfad = ReadLine("Lokaler Pfad (" + pfad + "): ");
                if (pfad == "")
                {
                    pfad = PFAD_DEFAULT;
                }
            }

            try 
            { 
                client.syncFiles(pfad);
            }
            catch (WebException webException)
            {
                if (webException.Message.Contains("Unauthorized"))
                {
                    client.setWebClient(getCookie(true));
                    try
                    {
                        client.syncFiles(pfad);
                    }
                    catch (WebException ex)
                    {
                        Console.WriteLine("Fehler: " + ex.Message);
                    }
                }
                else
                {
                    Console.WriteLine("Fehler: " + webException.Message);
                }
            }

            using (StreamWriter sw = new StreamWriter("config.ini", false))
            {
                sw.WriteLine("Cookie|" + SessionCookie);
                sw.WriteLine("Pfad|" + pfad);
                sw.WriteLine("StudIP|" + StudIpURL);
                sw.WriteLine("express|" + express.ToString());
                sw.WriteLine("ignore|" + ignore);
                sw.WriteLine("downloadOverwrite|" + downloadOverwrite);
                sw.WriteLine("User|" + username);
                if (savePass)
                {
                    sw.WriteLine("Pass|" + password);
                }
                sw.WriteLine("savePass|" + savePass);
            }
        }

        static Cookie getCookie(bool forceNew = false)
        {
            if (SessionCookie == "" || forceNew)
            {
                SessionCookie = ReadLine("Seminar_Session Cookie aus dem Browser: ");
            }
            return new Cookie("Seminar_Session", SessionCookie, "/", new Uri(StudIpURL).Host);
        }

        static string getStudIP()
        {
            if (StudIpURL == "")
            {
                StudIpURL = ReadLine("StudIP URL (https://elearning.uni-oldenburg.de/): ");
            }
            return StudIpURL;
        }

        static string ReadLine(string outp)
        {
            Console.WriteLine(outp);
            return Console.ReadLine();
        }
    }
}
