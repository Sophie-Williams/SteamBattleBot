﻿using System;
using SteamKit2;
using System.IO;
using System.Threading;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace SteamBattleBot
{
    class SteamFunctions
    {
        SteamClient steamClient;
        CallbackManager manager;
        SteamUser steamUser;
        SteamFriends steamFriends;


        bool isRunning, logCommands = false;

        public string user,
                      pass;

        string[] lines;

        string authCode, twoFactorAuth;
        byte[] sentryHash;

        int playerCount = 0;
        Structures.PlayerStructure selectedPlayer;

        string[] lastCommits = new string[5];

        Random _random = new Random();

        public List<Structures.PlayerStructure> players = new List<Structures.PlayerStructure>();
        List<ulong> admins = new List<ulong>();

        JsonSerializer serializer = new JsonSerializer();

        public void SteamLogIn()
        {
            steamClient = new SteamClient();
            manager = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>();

            steamFriends = steamClient.GetHandler<SteamFriends>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
            manager.Subscribe<SteamFriends.FriendMsgCallback>(OnChatMessage);

            manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsAdded);

            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

            #region Get the last 5 Github commits
            try
            {
                using (HttpClient client = new HttpClient())
                {

                    client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; WOW64; Trident/6.0)");

                    using (var response = client.GetAsync("https://api.github.com/repos/nickthegamer5/SteamBattleBot/commits").Result)
                    {
                        var json = response.Content.ReadAsStringAsync().Result;

                        dynamic commits = JArray.Parse(json);
                        lastCommits[0] = commits[0].commit.message;
                        lastCommits[1] = commits[1].commit.message;
                        lastCommits[2] = commits[2].commit.message;
                        lastCommits[3] = commits[3].commit.message;
                        lastCommits[4] = commits[4].commit.message;

                        Console.WriteLine("Github commits have been successfully stored!");
                    }

                }
            }catch(Exception e)
            {
                Console.WriteLine("Failed to connect to Github API; !changelog will not work!\nError Code: {0}", e.ToString());
            }
            #endregion

            #region Open players.json and parse
            try
            {
                Console.WriteLine("Parsing players.json...");
                using (StreamReader file = File.OpenText(@"players.json"))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    players = JsonConvert.DeserializeObject<List<Structures.PlayerStructure>>(file.ReadToEnd());
                }
            }catch(FileLoadException e)
            {
                Console.WriteLine("Faild to read players.json!\n" + e.ToString());
            }catch (FileNotFoundException e)
            {
                Console.WriteLine("Faild to find players.json!\n" + e.ToString());
            }catch (Exception e)
            {
                Console.WriteLine("An error occured while parsing players.json!\n" + e.ToString());
            }
            #endregion

            #region Read admin.txt
            try
            {
                lines = File.ReadAllLines("admin.txt"); // Read all the SteamID64s in the file
                admins.Clear(); // Clear the list to store the new admins

                for (int i = 0; i<lines.Length; i++) // Read each line in var
                {
                    try // Try to convert it
                    {
                        admins.Add(Convert.ToUInt64(lines[i])); // Add ID to list
                    }
                    catch // If invalid...
                    {
                        Console.WriteLine("WARNING: {0} is an invalid steamID16 in admin.txt! Skipping...", lines[i]); // Tell the user
                    }
                }
            }catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
            #endregion

            isRunning = true;

            Console.WriteLine("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
            Console.ReadKey();
        }

        void OnConnected(SteamClient.ConnectedCallback callBack)
        {

            if (callBack.Result != EResult.OK)
            {
                Console.WriteLine("Unable to connect to Steam: {0}", callBack.Result);
                isRunning = false;
                return;
            }

            Console.WriteLine("Connected to Steam! Logging in {0}...", user);

            if (File.Exists("sentry.bin"))
            {
                byte[] sentryFile = File.ReadAllBytes("sentry.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                Password = pass,
                AuthCode = authCode,
                TwoFactorCode = twoFactorAuth,
                SentryFileHash = sentryHash,
            });
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callBack)
        {
            Console.WriteLine("{0} disconnected from Steam, reconnecting in 5...", user);

            Thread.Sleep(TimeSpan.FromSeconds(5));

            steamClient.Connect();
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callBack)
        {
            bool isSteamGuard = callBack.Result == EResult.AccountLogonDenied;
            bool is2FA = callBack.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            if (isSteamGuard || is2FA)
            {
                Console.WriteLine("This account is SteamGuard protected!");

                if (is2FA)
                {
                    Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = Console.ReadLine();
                }
                else
                {
                    Console.Write("Please enter the auth code sent to the email at {0}: ", callBack.EmailDomain);
                    authCode = Console.ReadLine();
                }
                return;
            }
            if (callBack.Result != EResult.OK)
            {
                Console.WriteLine("Unable to logon to Steam: {0} / {1}", callBack.Result, callBack.ExtendedResult);

                isRunning = false;
                return;
            }

            Console.WriteLine("Successfully logged on!");
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callBack)
        {
            Console.WriteLine("Logged off of Steam: {0}", callBack.Result);
        }

        void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callBack)
        {
            Console.WriteLine("Updating sentry file...");
            int fileSize;
            byte[] sentryHash;

            using (var fs = File.Open("sentry.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.Seek(callBack.Offset, SeekOrigin.Begin);
                fs.Write(callBack.Data, 0, callBack.BytesToWrite);
                fileSize = (int)fs.Length;

                fs.Seek(0, SeekOrigin.Begin);
                using (var sha = new SHA1CryptoServiceProvider())
                {
                    sentryHash = sha.ComputeHash(fs);
                }
            }

            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callBack.JobID,

                FileName = callBack.FileName,

                BytesWritten = callBack.BytesToWrite,
                FileSize = fileSize,
                Offset = callBack.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callBack.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            Console.WriteLine("Done.");
        }

        void OnAccountInfo(SteamUser.AccountInfoCallback callBack)
        {
            steamFriends.SetPersonaState(EPersonaState.Online);
        }

        void OnChatMessage(SteamFriends.FriendMsgCallback callBack)
        {
            string[] args;
            if (callBack.EntryType == EChatEntryType.ChatMsg)
            {
                if (callBack.Message.Length > 1 && callBack.Message.Remove(1) == "!")
                {
                    string command = callBack.Message;
                    if (callBack.Message.Contains(" "))
                    {
                        command = callBack.Message.Remove(callBack.Message.IndexOf(" "));
                    }

                    // To use the args system use the following formate in the command
                    // args = seperate((amount of args it should have. example: 1), ' ', callBack.Message);
                    // It will return a list of the args; 0 being the command and 1, 2, etc being the args
                    // If their is no args found it will return -1 in slot [0] so make sure to use an if
                    // statement to check if their is more then one arg if your command uses it

                    foreach (Structures.PlayerStructure player in players) // Loop the list 
                    {
                        if (player.id == callBack.Sender.AccountID)
                        {
                            selectedPlayer = player;
                        }
                    }

                    switch (command)
                    {
                        #region !shutdown (Admin only)
                        case "!shutdown":
                            if (!IsBotAdmin(callBack.Sender))
                            {
                                SendUserMessage(callBack.Sender, "Only admins can use the !shutdown command!");
                                break;
                            }
                            Environment.Exit(0);
                            break;
                        #endregion

                        #region !resetadmins (Admin only)
                        case "!resetadmins":
                            if (!IsBotAdmin(callBack.Sender))
                            {
                                SendUserMessage(callBack.Sender, "Only admins can use the !resetadmins command!");
                                break;
                            }
                            try
                            {
                                Log(string.Format("!resetadmins command recived. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));
                                SendUserMessage(callBack.Sender, "Rechecking admin.txt...");

                                lines = File.ReadAllLines("admin.txt"); // Read all the SteamID64s in the file
                                admins.Clear(); // Clear the list to store the new admins
                                Console.WriteLine(lines);

                                for (int i = 0; i < lines.Length; i++) // Read each line in var
                                {
                                    try // Try to convert it
                                    {
                                        admins.Add(Convert.ToUInt64(lines[i])); // Add ID to list
                                    }
                                    catch // If invalid...
                                    {
                                        Console.WriteLine("WARNING: {0} is an invalid steamID16 in admin.txt! Skipping..."); // Tell the user
                                    }
                                }
                                SendUserMessage(callBack.Sender, "Done!");
                            } catch (Exception e)
                            {
                                Console.WriteLine("Error while checking admin.txt: {0}", e.ToString());
                                SendUserMessage(callBack.Sender, string.Format("Error while checking admin.txt: {0}", e.ToString()));
                            }

                            break;
                        #endregion

                        #region !debug [setting] (Admin only)
                        case "!debug":
                            if (!IsBotAdmin(callBack.Sender))
                            {
                                SendUserMessage(callBack.Sender, "Only admins can use the !shutdown command!");
                                break;
                            }
                            args = Seperate(1, ' ', callBack.Message);
                            if (args[0] == "-1")
                            {
                                SendUserMessage(callBack.Sender, "Command syntax: !debug (setting)");
                                return;
                            }


                            if (args[1] == "logcommands")
                            {
                                logCommands = !logCommands;
                                SendUserMessage(callBack.Sender, string.Format("Debug mode logchat: {0}", logCommands ? "Enabled" : "Disabled"));
                            }

                            Log(string.Format("!log {0} command recived. User: {1}", args[1], steamFriends.GetFriendPersonaName(callBack.Sender)));

                            break;
                        #endregion

                        #region !message [message] (Admin only)
                        case "!message": //!message (message)
                            if (!IsBotAdmin(callBack.Sender))
                                break;
                            args = Seperate(1, ' ', callBack.Message);
                            if (args[0] == "-1")
                            {
                                SendUserMessage(callBack.Sender, "Command syntax: !message (message)");
                                return;
                            }
                            Log("!message command recived. User: " + steamFriends.GetFriendPersonaName(callBack.Sender));
                            for (int i = 0; i < steamFriends.GetFriendCount(); i++)
                            {
                                SteamID friend = steamFriends.GetFriendByIndex(i);
                                SendUserMessage(friend, args[1]);
                            }
                            break;
                        #endregion

                        #region !changename [name] (Admin only)
                        case "!changename":
                            if (!IsBotAdmin(callBack.Sender))
                                return;
                            args = Seperate(1, ' ', callBack.Message);
                            Log("!changename " + args[1] + " command recieved. User: " + steamFriends.GetFriendPersonaName(callBack.Sender));
                            if (args[0] == "-1")
                            {
                                SendUserMessage(callBack.Sender, "Syntax: !changename [name]");
                                return;
                            }
                            steamFriends.SetPersonaName(args[1]);
                            break;
                        #endregion

                        #region !save (Admin only)
                        case "!save":
                            if (!IsBotAdmin(callBack.Sender))
                                return;
                            Log(string.Format("!save command revied. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));
                            try
                            {
                                Console.WriteLine("Player data is being saved...");
                                SendUserMessage(callBack.Sender, "Saving player data...");
                                using (StreamWriter sw = new StreamWriter(@"players.json"))
                                    sw.Write(JsonConvert.SerializeObject(players, Formatting.Indented));
                                Console.WriteLine("Player data has been saved successfully!");
                                SendUserMessage(callBack.Sender, "Player data has been saved successfully!");
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Error while trying to save the player data!\n" + e.ToString());
                                SendUserMessage(callBack.Sender, "Error occured while trying to save the data! Please check the console for more info.");
                            }
                            break;
                        #endregion

                        #region !setup
                        case "!setup":
                            playerCount = players.Count;
                            if (IsAlreadyPlayer(callBack))
                            {
                                Log(string.Format("!setup command recived. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));
                                SendUserMessage(callBack.Sender, "You are already setup! Type !restart to restart your game.");
                                return;
                            }
                            SendUserMessage(callBack.Sender, "Setting up user...");
                            Log(string.Format("!setup command recived. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));

                            players.Add(new Structures.PlayerStructure { id = callBack.Sender.AccountID });

                            selectedPlayer.HandleMessage(callBack, steamFriends, command);

                            break;

                        #endregion

                        #region !changelog
                        case "!changelog":
                            Log(string.Format("!changelog command recived. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));
                            SendUserMessage(callBack.Sender, string.Format("\nLast 5 commits from Github:\n\n{0}\n{1}\n{2}\n{3}\n{4}\n\nPlease check out the Github page\nhttps://github.com/nickthegamer5/SteamBattleBot", lastCommits[0], lastCommits[1], lastCommits[2], lastCommits[3], lastCommits[4]));
                            break;
                        #endregion

                        #region !help
                        case "!help":
                            Log(string.Format("!help command revied. User: {0}", steamFriends.GetFriendPersonaName(callBack.Sender)));
                            SendUserMessage(callBack.Sender,
                                "\nThe current commands are:\n" +
                                "!help\n" +
                                "!attack\n" +
                                "!block\n" +
                                "!setup\n" +
                                "!stats\n" +
                                "!shop\n" +
                                "!changelog\n" +
                                "!shutdown (admin only)\n" +
                                "!message [message] (admin only)\n" +
                                "!changename [name] (admin only)\n" +
                                "!debug [setting] (admin only)\n" +
                                "!resetadmins (admin only)" +
                                "!save (admin only)");
                            break;
                        #endregion

                        default:
                            selectedPlayer.HandleMessage(callBack, steamFriends, command);
                            break;

                    }
                }
                foreach (Structures.PlayerStructure player in players) // Loop the list
                {
                    if (player.id == callBack.Sender.AccountID && callBack.Message.Length == 1 && player.shopMode == true)
                    {
                        player.ProcessShop(callBack.Message, callBack, steamFriends);
                    }
                }
            }
        }

        void SendUserMessage(SteamID callBack, string message)
        {
            steamFriends.SendChatMessage(callBack, EChatEntryType.ChatMsg, message);
        }

        bool IsAlreadyPlayer(SteamFriends.FriendMsgCallback callBack)
        {
            foreach (Structures.PlayerStructure player in players)
            {
                if (player.id == callBack.Sender.AccountID)
                {
                    return true;
                }
            }
            return false;
        }

        bool IsBotAdmin(SteamID sid, bool refresh = false)
        {
            foreach (ulong id in admins) // Loop the list
            {
                if (sid.ConvertToUInt64() == id) // Convert the users steamID and see if it matches...
                {
                    return true; // Return true if id matched
                }
            }
            // If their ID was not found, tell the user
            SendUserMessage(sid, "You are not a bot admin.");
            // And put the message in console
            Log(string.Format("{0} attempted to use an administrator command while not an administrator.", steamFriends.GetFriendPersonaName(sid)));
            return false;
        }

        void OnFriendsAdded(SteamFriends.FriendsListCallback callBack)
        {
            Thread.Sleep(2500);

            foreach (var friend in callBack.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    steamFriends.AddFriend(friend.SteamID);
                    Thread.Sleep(2000);
                    SendUserMessage(friend.SteamID, "Hello, I am the STEAM Battle Bot. Type !setup to start the battle!");
                    SendUserMessage(friend.SteamID, "Type !help for commands related to the bot!");
                }
            }
        }

        string[] Seperate(int number, char seperator, string thestring)
        {
            string[] returned = new string[4];

            int i = 0;
            int error = 0;
            int length = thestring.Length;

            foreach (char c in thestring)
            {
                if (i != number)
                {
                    if (error > length || number > 5)
                    {
                        returned[0] = "-1";
                        return returned;
                    }
                    else if (c == seperator)
                    {
                        returned[i] = thestring.Remove(thestring.IndexOf(c));
                        thestring = thestring.Remove(0, thestring.IndexOf(c) + 1);
                        i++;
                    }
                    error++;

                    if (error == length && i != number)
                    {
                        returned[0] = "-1";
                        return returned;
                    }
                }
                else
                {
                    returned[i] = thestring;
                }
            }
            return returned;
        }

        public string InputPass()
        {
            {
                string pass = "";
                bool protectPass = true;
                while (protectPass)
                {
                    char s = Console.ReadKey(true).KeyChar;
                    if (s == '\r')
                    {
                        protectPass = false;
                        Console.WriteLine();
                    }
                    else if (s == '\b' && pass.Length > 0)
                    {
                        Console.CursorLeft -= 1;
                        Console.Write(' ');
                        Console.CursorLeft -= 1;
                        pass = pass.Substring(0, pass.Length - 1);
                    }
                    else
                    {
                        pass = pass + s.ToString();
                        Console.Write("*");
                    }

                }
                return pass;
            }
        }

        public void Log(string command, bool force = false)
        {
            if (logCommands || force)
                Console.WriteLine(DateTime.Now.ToString("[hh:mm:ss] ") + command);
        }
    }
}
