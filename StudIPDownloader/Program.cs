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
        static void Main(string[] args)
        {
            string pfad = PFAD_DEFAULT;
            string SessionCookie = "";
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
                    }
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine("config.ini konnte nicht gelesen werden.");
            }

            StudIPClient client = new StudIPClient(getCookie(SessionCookie), "https://elearning.uni-oldenburg.de/");
            
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
                    client.syncFiles(pfad);
                }
            }
        }

        static Cookie getCookie(string SessionCookie = "")
        {
            if (SessionCookie == "")
            {
                SessionCookie = ReadLine("Seminar_Session Cookie aus dem Browser: ");
            }
            return new Cookie("Seminar_Session", SessionCookie, "/", "elearning.uni-oldenburg.de");
        }

        static string ReadLine(string outp)
        {
            Console.WriteLine(outp);
            return Console.ReadLine();
        }
    }
}
