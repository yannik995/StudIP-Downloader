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
        static string StudIpURL = "";
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
                        if (line.StartsWith("Pfad|"))
                        {
                            pfad = line.Split('|')[1];
                        }
                        if (line.StartsWith("StudIP|"))
                        {
                            StudIpURL = line.Split('|')[1];
                        }
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("config.ini konnte nicht gelesen werden.");
            }

            StudIPClient client = new StudIPClient(getStudIP(), getCookie());
            
            if(pfad == PFAD_DEFAULT)
            {
                pfad = ReadLine("Lokaler Pfad (" + pfad + "): ");
                if (pfad == "")
                {
                    pfad = PFAD_DEFAULT;
                }
            }

            using (StreamWriter sw = new StreamWriter("config.ini", false))
            {
                sw.WriteLine("Cookie|" + SessionCookie);
                sw.WriteLine("Pfad|" + pfad);
                sw.WriteLine("StudIP|" + StudIpURL);
            }
            try 
            { 
                client.syncFiles(pfad);
            }
            catch (WebException webException)
            {
                if (webException.Message.Contains("(401) Unauthorized"))
                {
                    client.setWebClient(getCookie());
                    try
                    {
                        client.syncFiles(pfad);
                    }
                    catch (WebException ex)
                    {
                        Console.WriteLine("Fehler: " + ex.Message);
                    }
                }
            }
        }

        static Cookie getCookie()
        {
            if (SessionCookie == "")
            {
                SessionCookie = ReadLine("Seminar_Session Cookie aus dem Browser: ");
            }
            return new Cookie("Seminar_Session", SessionCookie, "/", "elearning.uni-oldenburg.de");
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
