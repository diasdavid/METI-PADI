﻿using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Collections;
using System.Net.Sockets;
using log4net;

namespace Puppet_Master
{
    public partial class puppetMasterGUI : Form
    {
        /****************************************************************************************
         *                                  Attributes
         ****************************************************************************************/

        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


        int firstMetaServerPort = 8000;
        int firstClientPort = 8100;
        int firstDataServerPort = 9000;

        int nbDataServers;
        int nbClients;

        String[] listOfDataServerPorts = new String[1];
        String[] listOfMetaServerPorts;
        String[] listOfClientPorts = new String[1];


        String[] listOfMetaServerBackdoorPorts = new String[3];
        String[] listOfDataServerBackdoorPorts;

        // Dictonary of Running Processes Key=processID (e.g. c-1) Value=Process
        public Dictionary<string, Process> runningProcesses = new Dictionary<string, Process>();

        TcpChannel channel;


        /****************************************************************************************
        *                                  GUI functions
        ****************************************************************************************/


        public puppetMasterGUI()
        {
            InitializeComponent();
            /* Initialize TCP Channel */
            channel = new TcpChannel();
            ChannelServices.RegisterChannel(channel, false);
        }

        /**
         * Opens a Script File and puts it's steps onto Script Text Box
         */
        private void openScriptFile_Click(object sender, EventArgs e)
        {
            var FD = new System.Windows.Forms.OpenFileDialog();
            if (FD.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string fileToOpen = FD.FileName;
                String[] allLines = File.ReadAllLines(fileToOpen);
                scriptTextBox.Lines = allLines;
            }
        }

        private void runNextStep_Click(object sender, EventArgs e)
        {
            executeNextStep();
        }


        private void runScript_Click(object sender, EventArgs e)
        {
            while (true)
            {
                String[] lines = scriptTextBox.Lines;
                try { if (lines[0] == "") { return; } }
                catch (IndexOutOfRangeException) { return; }
                executeNextStep();
                System.Threading.Thread.Sleep(5000);
            }
        }


        /* check if process is running */
        private Boolean isRunning(string process)
        {
            return runningProcesses.ContainsKey(process);
        }

        /* check there are metaservers already running */
        private bool thereAreMetaServers()
        {
            foreach (string key in runningProcesses.Keys)
                if (key.StartsWith("m-"))
                    return true;
            return false;
        }

        /****************************************************************************************
         *                                  Logic functions
         ****************************************************************************************/

        private void executeNextStep()
        {
            //Read Next Line from Input
            //Parse it
            //Process it
            String[] lines = scriptTextBox.Lines;
            try { if (lines[0] == "") { return; } }
            catch (IndexOutOfRangeException) { return; }
            String nextSept = lines[0];

            lines = lines.Where((val, idx) => idx != 0).ToArray();
            scriptTextBox.Lines = lines;

            if (nextSept.StartsWith("#"))
                return;
            currentStep.Text = nextSept;



            String[] p = { " ", "\t", ", " };
            string[] parsed = nextSept.Split(p, StringSplitOptions.None);

            switch (parsed[0])
            {
                case "START": start(Convert.ToInt32(parsed[1]), Convert.ToInt32(parsed[2])); break;
                case "FAIL": fail(parsed[1]); break;
                case "RECOVER": recover(parsed[1]); break;
                case "FREEZE": freeze(parsed[1]); break;
                case "UNFREEZE": unfreeze(parsed[1]); break;
                case "CREATE": create(parsed[1], parsed[2], Convert.ToInt32(parsed[3]), Convert.ToInt32(parsed[4]), Convert.ToInt32(parsed[5])); break;
                case "DELETE": delete(parsed[1], parsed[2]); break;
                case "OPEN": open(parsed[1], parsed[2]); break;
                case "CLOSE": close(parsed[1], parsed[2]); break;
                case "READ": read(parsed[1], Convert.ToInt32(parsed[2]), parsed[3], Convert.ToInt32(parsed[4])); break;
                case "WRITE":
                    if (parsed[3].StartsWith("\""))
                    {
                        for (int i = 4; i < parsed.Length; i++)
                            parsed[3] += " " + parsed[i];
                        write(parsed[1], Convert.ToInt32(parsed[2]), parsed[3]);
                    }
                    else
                    {
                        write(parsed[1], Convert.ToInt32(parsed[2]), Convert.ToInt32(parsed[3]));
                    }
                    break;
                case "DUMP": dump(parsed[1]); break;
                case "COPY":
                    for (int i = 6; i < parsed.Length; i++)
                        parsed[5] += " " + parsed[i];
                    copy(parsed[1], Convert.ToInt32(parsed[2]), parsed[3], Convert.ToInt32(parsed[4]), parsed[5]);
                    break;
                case "TRANSFER": transfer(parsed[1], parsed[2], parsed[3]); break;
                case "EXESCRIPT": exeScript(parsed[1], parsed[2]); break;
                case "SWITCHLOADBALANCE": loadbalanceSwitch(parsed[1], Convert.ToInt32(parsed[2])); break;
                case "LOADBALANCE": loadbalance(parsed[1]); break;
                case "HELLO": hello(parsed[1]); break;
                case "LBDUMP": loadBalanceDump(parsed[1]); break;
                case "SLEEP": Thread.Sleep(Convert.ToInt32(parsed[1])); break;
            }
        }


        private void start(int nbClients, int nbDataServers)
        {
            //Start DataServers
            //Start Meta-Data Servers (3)
            //Start Clients
            String dataServerPath = Environment.CurrentDirectory.Replace("Puppet Master", "Data-Server");
            dataServerPath += "/Data-Server.exe";
            String metaServerPath = Environment.CurrentDirectory.Replace("Puppet Master", "Meta-Data Server");
            metaServerPath += "/Meta-Data Server.exe";
            String clientPath = Environment.CurrentDirectory.Replace("Puppet Master", "Client");
            clientPath += "/Client.exe";

            //outputBox.Text = dataServerPath;    

            String listOfDataServerPorts = "";
            String listOfMetaServerPorts = "";
            String listOfClientPorts = "";


            //Data-Servers - Args <PortLocal>
            for (int i = 0; i < nbDataServers; i++)
            {
                runningProcesses.Add("d-" + i, new Process());
                runningProcesses["d-" + i].StartInfo.Arguments = (firstDataServerPort + i).ToString() + " " + i;
                listOfDataServerPorts += (firstDataServerPort + i).ToString() + " ";
                runningProcesses["d-" + i].StartInfo.FileName = dataServerPath;
                runningProcesses["d-" + i].Start();
                Console.WriteLine("Data-Server Started");
                System.Threading.Thread.Sleep(500);
            }




            //Meta-Data Servers - Args <MetaDataPortLocal> <MetaDataPortOtherA> <MetaDataPortOtherB> [DataServerPort] [DataServer Port] [DataServer Port]...
            String meta0 = (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 1).ToString() + " " + (firstMetaServerPort + 2).ToString();
            runningProcesses.Add("m-" + 0, new Process());
            runningProcesses["m-" + 0].StartInfo.Arguments = meta0 + " " + listOfDataServerPorts;
            runningProcesses["m-" + 0].StartInfo.FileName = metaServerPath;
            runningProcesses["m-" + 0].Start();
            Console.WriteLine("Meta-Server 0 Started");
            System.Threading.Thread.Sleep(500);


            String meta1 = (firstMetaServerPort + 1).ToString() + " " + (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 2).ToString();
            runningProcesses.Add("m-" + 1, new Process());
            runningProcesses["m-" + 1].StartInfo.Arguments = meta1 + " " + listOfDataServerPorts;
            runningProcesses["m-" + 1].StartInfo.FileName = metaServerPath;
            runningProcesses["m-" + 1].Start();

            Console.WriteLine("Meta-Server 1 Started");
            System.Threading.Thread.Sleep(500);

            String meta2 = (firstMetaServerPort + 2).ToString() + " " + (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 1).ToString();
            runningProcesses.Add("m-" + 2, new Process());
            runningProcesses["m-" + 2].StartInfo.Arguments = meta2 + " " + listOfDataServerPorts;
            runningProcesses["m-" + 2].StartInfo.FileName = metaServerPath;
            runningProcesses["m-" + 2].Start();

            Console.WriteLine("Meta-Server 2 Started");
            System.Threading.Thread.Sleep(500);


            listOfMetaServerPorts = meta0; //Meta0 Contem a ordem certa de Meta-Servers que corresponde as responsabilidades para serem entregues aos clientes

            //Clients - Args <clientPort> <clientID> <meta0Port> <meta1Port> <meta2Port> 
            for (int k = 0; k < nbClients; k++)
            {
                runningProcesses.Add("c-" + k, new Process());
                runningProcesses["c-" + k].StartInfo.Arguments = (firstClientPort + k).ToString() + " " + ("c-" + k + " ") + listOfMetaServerPorts;
                listOfClientPorts += (firstClientPort + k).ToString() + " ";
                runningProcesses["c-" + k].StartInfo.FileName = clientPath;
                runningProcesses["c-" + k].Start();
            }

            this.nbClients = nbClients;
            this.nbDataServers = nbDataServers;

            this.listOfDataServerPorts = listOfDataServerPorts.Split(' ');
            this.listOfMetaServerPorts = listOfMetaServerPorts.Split(' ');
            this.listOfClientPorts = listOfClientPorts.Split(' ');

            for (int i = 0; i < this.listOfMetaServerPorts.Length; i++)
            {
                this.listOfMetaServerBackdoorPorts[i] = (Convert.ToInt32(this.listOfMetaServerPorts[i]) + 2000).ToString();
            }

            this.listOfDataServerBackdoorPorts = this.listOfDataServerPorts;

            for (int i = 0; i < this.listOfDataServerBackdoorPorts.Length - 1; i++)
            {
                this.listOfDataServerBackdoorPorts[i] = (Convert.ToInt32(this.listOfDataServerPorts[i]) + 100).ToString();
            }

        }

        private void startAlone(string process)
        {
            // is already checked before call this if the process is already started

            int processNum = int.Parse(process[2].ToString());

            this.listOfDataServerPorts[0] = firstDataServerPort.ToString();
            this.listOfClientPorts[0] = firstClientPort.ToString();


            // Metadata Servers
            if (process.StartsWith("m-"))
            {
                String metaServerPath = Environment.CurrentDirectory.Replace("Puppet Master", "Meta-Data Server");
                metaServerPath += "/Meta-Data Server.exe";

                string listOfMetas = (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 1).ToString() + " " + (firstMetaServerPort + 2).ToString();
                string meta = listOfMetas;
                switch (processNum)
                {
                    case 0: meta = (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 1).ToString() + " " + (firstMetaServerPort + 2).ToString(); break;
                    case 1: meta = (firstMetaServerPort + 1).ToString() + " " + (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 2).ToString(); break;
                    case 2: meta = (firstMetaServerPort + 2).ToString() + " " + (firstMetaServerPort + 0).ToString() + " " + (firstMetaServerPort + 1).ToString(); break;
                }

                runningProcesses.Add("m-" + processNum, new Process());

                string dports = "";
                for (int i = 0; i < listOfDataServerPorts.Length; i++)
                {
                    dports += listOfDataServerPorts[i];
                    if (i != (listOfDataServerPorts.Length - 1))
                        dports += " ";
                }

                runningProcesses["m-" + processNum].StartInfo.Arguments = meta + " " + dports;
                runningProcesses["m-" + processNum].StartInfo.FileName = metaServerPath;
                runningProcesses["m-" + processNum].Start();
                System.Threading.Thread.Sleep(100);

                this.listOfMetaServerPorts = listOfMetas.Split(' ');

                this.listOfDataServerBackdoorPorts = this.listOfDataServerPorts;

                for (int i = 0; i < this.listOfMetaServerPorts.Length; i++)
                {
                    this.listOfMetaServerBackdoorPorts[i] = (Convert.ToInt32(this.listOfMetaServerPorts[i]) + 2000).ToString();
                }

            }

            // Data Servers
            if (process.StartsWith("d-"))
            {
                String dataServerPath = Environment.CurrentDirectory.Replace("Puppet Master", "Data-Server");
                dataServerPath += "/Data-Server.exe";

                runningProcesses.Add("d-" + processNum, new Process());
                runningProcesses["d-" + processNum].StartInfo.Arguments = (firstDataServerPort + processNum).ToString() + " " + processNum;
                runningProcesses["d-" + processNum].StartInfo.FileName = dataServerPath;
                runningProcesses["d-" + processNum].Start();
                Console.WriteLine("Data-Server Started");
                System.Threading.Thread.Sleep(500);

                String[] dataPorts = this.listOfDataServerPorts;

                if (this.listOfDataServerPorts.Length <= processNum)
                {
                    dataPorts = new String[processNum + 1];
                    this.listOfDataServerPorts.CopyTo(dataPorts, 0);
                }
                dataPorts[processNum] = (firstDataServerPort + processNum).ToString();
                this.listOfDataServerPorts = dataPorts;
                /*
                this.listOfDataServerBackdoorPorts = this.listOfDataServerPorts;

                for (int i = 0; i < this.listOfDataServerPorts.Length-1; i++)
                {
                    this.listOfDataServerBackdoorPorts[i] = (Convert.ToInt32(this.listOfDataServerPorts[i]) + 100).ToString();
                }*/

                listOfDataServerBackdoorPorts = new string[listOfDataServerPorts.Length];
                for (int i = 0; i < this.listOfDataServerPorts.Length; i++)
                {
                    this.listOfDataServerBackdoorPorts[i] = (Convert.ToInt32(this.listOfDataServerPorts[i]) + 2000).ToString();
                }
            }

            // Client
            if (process.StartsWith("c-"))
            {
                String clientPath = Environment.CurrentDirectory.Replace("Puppet Master", "Client");
                clientPath += "/Client.exe";
                runningProcesses.Add("c-" + processNum, new Process());
                runningProcesses["c-" + processNum].StartInfo.Arguments = (firstClientPort + processNum).ToString() + " " + ("c-" + processNum + " ") + this.listOfMetaServerPorts[0] + " " + this.listOfMetaServerPorts[1] + " " + this.listOfMetaServerPorts[2];
                runningProcesses["c-" + processNum].StartInfo.FileName = clientPath;
                runningProcesses["c-" + processNum].Start();

                String[] cliPorts = this.listOfClientPorts;
                if (this.listOfClientPorts.Length <= processNum)
                {
                    cliPorts = new String[processNum + 1];
                    this.listOfClientPorts.CopyTo(cliPorts, 0);
                }
                cliPorts[processNum] = (firstClientPort + processNum).ToString();
                this.listOfClientPorts = cliPorts;
            }
        }


        /****************************************************************************************
         *                                  Remote Invocations
         ****************************************************************************************/

        //Our Delegate to call Assynchronously remote methods
        public delegate void RemoteAsyncDelegate(); //Used for fail;recover;freeze;unfreeze
        public delegate void CreateRemoteAsyncDelegate(string filename, int nbDataServers, int readQuorum, int writeQuorum);
        public delegate void WriteRemoteAsyncDelegate(int fileregister, int bytearrayregister);
        public delegate void WriteWithContentRemoteAsyncDelegate(int fileregister, byte[] bytearray);
        public delegate void OpenRemoteAsyncDelegate(string filename);
        public delegate void CloseRemoteAsyncDelegate(string filename);
        public delegate void DeleteRemoteAsyncDelegate(string filename);
        public delegate void ReadRemoteAsyncDelegate(int fileregister, int semantics, int bytearrayregister);
        public delegate TransactionDTO TransferRemoteAsyncDelegate(TransactionDTO dto, string address);
        public delegate void CopyRemoteAsyncDelegate(int fileregister1, int semantics, int fileregister2, string salt);
        public delegate void ExecRemoteAsyncDelegate(List<string> filename);
        public delegate void SwitchLoadBalancingRemoteAsyncDelegate(bool sw);
        public delegate void LoadBalancingRemoteAsyncDelegate();




        private void fail(string process)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            MyRemoteMetaDataInterface mdi;
            MyRemoteDataInterface dsi;

            // Metadata Servers
            if (process.StartsWith("m-"))
            {
                string backdoor = listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])];
                mdi = Utils.getRemoteMetaDataObj(backdoor);
                log.Info("Backdoor of meta " + backdoor);
                //mdi.fail();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(mdi.fail);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            // Data Servers
            else if (process.StartsWith("d-"))
            {
                string backdoor = listOfDataServerBackdoorPorts[(int)Char.GetNumericValue(process[2])];
                dsi = Utils.getRemoteDataServerObj(backdoor);
                log.Info("Backdoor of data " + backdoor);
                //dsi.fail();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(dsi.fail);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            else
                outputBox.Text = "Cannot fail the process " + process;
        }

        private void recover(string process)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            MyRemoteMetaDataInterface mdi;
            MyRemoteDataInterface dsi;

            // Metadata Servers
            if (process.StartsWith("m-"))
            {
                mdi = Utils.getRemoteMetaDataObj(listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                //mdi.recover();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(mdi.recover);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            // Data Servers
            if (process.StartsWith("d-"))
            {
                dsi = Utils.getRemoteDataServerObj(listOfDataServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                //dsi.recover();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(dsi.recover);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
        }

        private void freeze(string process)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            MyRemoteDataInterface dsi;
            // Data Servers
            if (process.StartsWith("d-"))
            {
                dsi = Utils.getRemoteDataServerObj(listOfDataServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                //dsi.freeze();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(dsi.freeze);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
                log.Info("Freeze sent to " + process);
            }
            else
                outputBox.Text = "Cannot freeze the process " + process;
        }

        private void unfreeze(string process)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            MyRemoteDataInterface dsi;

            // Data Servers
            if (process.StartsWith("d-"))
            {
                dsi = Utils.getRemoteDataServerObj(listOfDataServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                //dsi.unfreeze();
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(dsi.unfreeze);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
                log.Info("Unfreeze sent to " + process);
            }
            else
                outputBox.Text = "Cannot unfreeze the process " + process;
        }


        private void create(string process, string filename, int nbDataServers, int readQuorum, int writeQuorum)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.create(filename, nbDataServers, readQuorum, writeQuorum);
            CreateRemoteAsyncDelegate RemoteDel = new CreateRemoteAsyncDelegate(rci.create);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(filename, nbDataServers, readQuorum, writeQuorum, null, null);
        }



        private void open(string process, string filename)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.open(filename);
            OpenRemoteAsyncDelegate RemoteDel = new OpenRemoteAsyncDelegate(rci.open);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(filename, null, null);

        }


        private void close(string process, string filename)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.close(filename); 
            OpenRemoteAsyncDelegate RemoteDel = new OpenRemoteAsyncDelegate(rci.close);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(filename, null, null);
        }

        private void delete(string process, string filename)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.create(filename, nbDataServers, readQuorum, writeQuorum);
            DeleteRemoteAsyncDelegate RemoteDel = new DeleteRemoteAsyncDelegate(rci.delete);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(filename, null, null);
            return;
        }

        private void read(string process, int reg, string semantics, int byteArrayRegister)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            int DEFAULT = 1;
            int MONOTONIC = 2;
            int semantic;

            switch (semantics)
            {
                case "default": semantic = DEFAULT; break;
                case "monotonic": semantic = MONOTONIC; break;
                default: semantic = DEFAULT; break;
            }

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.read(reg, semantic, byteArrayRegister);
            ReadRemoteAsyncDelegate RemoteDel = new ReadRemoteAsyncDelegate(rci.read);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(reg, semantic, byteArrayRegister, null, null);
        }


        private void write(string process, int reg, string content)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            Byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
            //rci.write(reg, bytes); 
            WriteWithContentRemoteAsyncDelegate RemoteDel = new WriteWithContentRemoteAsyncDelegate(rci.write);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(reg, bytes, null, null);
        }

        private void write(string process, int reg, int byteArrayRegister)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.write(reg, byteArray);
            WriteRemoteAsyncDelegate RemoteDel = new WriteRemoteAsyncDelegate(rci.write);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(reg, byteArrayRegister, null, null);
        }

        private void dump(string process)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            if (process.StartsWith("m-"))
            {
                MyRemoteMetaDataInterface mdi = Utils.getRemoteMetaDataObj(listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(mdi.dump);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            else if (process.StartsWith("d-"))
            {
                MyRemoteDataInterface rdi = Utils.getRemoteDataServerObj(listOfDataServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(rdi.dump);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            else if (process.StartsWith("c-"))
            {
                remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(rci.dump);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
        }

        private void transfer(string process, string filename, string destination_process)
        {

            TransactionDTO dto = new TransactionDTO(Utils.generateTransactionID(), "puppetmaster", filename);

            string port = listOfDataServerPorts[(int)Char.GetNumericValue(destination_process[2])];

            MyRemoteDataInterface rdi = Utils.getRemoteDataServerObj(listOfDataServerPorts[(int)Char.GetNumericValue(process[2])]);
            //rci.create(filename, nbDataServers, readQuorum, writeQuorum);
            TransferRemoteAsyncDelegate RemoteDel = new TransferRemoteAsyncDelegate(rdi.transferFile);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(dto, "tcp://localhost:" + port + "/sdasdssd", null, null);
        }


        private void copy(string process, int reg1, string semantics, int reg2, string salt)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            int DEFAULT = 1;
            int MONOTONIC = 2;
            int semantic;

            switch (semantics)
            {
                case "default": semantic = DEFAULT; break;
                case "monotonic": semantic = MONOTONIC; break;
                default: semantic = DEFAULT; break;
            }

            remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
            CopyRemoteAsyncDelegate RemoteDel = new CopyRemoteAsyncDelegate(rci.copy);
            IAsyncResult RemAr = RemoteDel.BeginInvoke(reg1, semantic, reg2, salt, null, null);

        }

        private void exeScript(string process, string scriptFile)
        {
            // verifies if process is already running, if not start it
            if (!isRunning(process))
                startAlone(process);

            List<string> commands = new List<string>();
            StreamReader sw = new StreamReader(scriptFile);
            while (!sw.EndOfStream)
                commands.Add(sw.ReadLine());
            sw.Close();

            // Metadata Servers
            if (process.StartsWith("c-"))
            {
                remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
                ExecRemoteAsyncDelegate RemoteExec = new ExecRemoteAsyncDelegate(rci.exeScript);
                IAsyncResult RemAr = RemoteExec.BeginInvoke(commands, null, null);
            }
            else
                outputBox.Text = "Cannot exeScript the process " + process + " because it isn't a client process";

        }

        /*Communication Testing Method*/
        private void hello(string process)
        {
            outputBox.Text = process;
            System.Threading.Thread.Sleep(500);
            if (process[0] == 'c')
            {
                remoteClientInterface rci = Utils.getRemoteClientObj(listOfClientPorts[(int)Char.GetNumericValue(process[2])]);
                string result = rci.metodoOla();
                outputBox.Text = result;
            }
            if (process[0] == 'm') { }
            if (process[0] == 'd') { }
        }

        private void loadbalanceSwitch(string process, int boolean)
        {
            bool sw;
            if (boolean == 1)
                sw = true;
            else
                sw = false;

            if (process.StartsWith("m-"))
            {
                MyRemoteMetaDataInterface mdi = Utils.getRemoteMetaDataObj(listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                SwitchLoadBalancingRemoteAsyncDelegate RemoteDel = new SwitchLoadBalancingRemoteAsyncDelegate(mdi.switchLoadBalancing);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(sw, null, null);
            }
            else
            {
                outputBox.Text = "Invalid process";
            }
        }


        private void loadbalance(string process)
        {
            if (process.StartsWith("m-"))
            {
                MyRemoteMetaDataInterface mdi = Utils.getRemoteMetaDataObj(listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                LoadBalancingRemoteAsyncDelegate RemoteDel = new LoadBalancingRemoteAsyncDelegate(mdi.loadBalancing);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            else
            {
                outputBox.Text = "Invalid process";
            }
        }


        private void loadBalanceDump(string process)
        {
            if (process.StartsWith("m-"))
            {
                MyRemoteMetaDataInterface mdi = Utils.getRemoteMetaDataObj(listOfMetaServerBackdoorPorts[(int)Char.GetNumericValue(process[2])]);
                RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(mdi.loadBalanceDump);
                IAsyncResult RemAr = RemoteDel.BeginInvoke(null, null);
            }
            else
            {
                outputBox.Text = "Invalid process";
            }
        }












        /* FOR EXAUSTIVE TESTING */

        private void button_Run_From_File(object sender, EventArgs e)
        {
            var FD = new System.Windows.Forms.OpenFileDialog();
            if (FD.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string fileToOpen = FD.FileName;
                String[] allLines = File.ReadAllLines(fileToOpen);

                foreach (String nextSept in allLines)
                {


                    if (nextSept.StartsWith("#"))
                        continue;
                    //currentStep.Text = nextSept;



                    String[] p = { " ", "\t", ", " };
                    string[] parsed = nextSept.Split(p, StringSplitOptions.None);

                    switch (parsed[0])
                    {
                        case "START": start(Convert.ToInt32(parsed[1]), Convert.ToInt32(parsed[2])); break;
                        case "FAIL": fail(parsed[1]); break;
                        case "RECOVER": recover(parsed[1]); break;
                        case "FREEZE": freeze(parsed[1]); break;
                        case "UNFREEZE": unfreeze(parsed[1]); break;
                        case "CREATE": create(parsed[1], parsed[2], Convert.ToInt32(parsed[3]), Convert.ToInt32(parsed[4]), Convert.ToInt32(parsed[5])); break;
                        case "DELETE": delete(parsed[1], parsed[2]); break;
                        case "OPEN": open(parsed[1], parsed[2]); break;
                        case "CLOSE": close(parsed[1], parsed[2]); break;
                        case "READ": read(parsed[1], Convert.ToInt32(parsed[2]), parsed[3], Convert.ToInt32(parsed[4])); break;
                        case "WRITE":
                            if (parsed[3].StartsWith("\""))
                            {
                                for (int i = 4; i < parsed.Length; i++)
                                    parsed[3] += " " + parsed[i];
                                write(parsed[1], Convert.ToInt32(parsed[2]), parsed[3]);
                            }
                            else
                            {
                                write(parsed[1], Convert.ToInt32(parsed[2]), Convert.ToInt32(parsed[3]));
                            }
                            break;
                        case "DUMP": dump(parsed[1]); break;
                        case "COPY":
                            for (int i = 6; i < parsed.Length; i++)
                                parsed[5] += " " + parsed[i];
                            copy(parsed[1], Convert.ToInt32(parsed[2]), parsed[3], Convert.ToInt32(parsed[4]), parsed[5]);
                            break;
                        case "TRANSFER": transfer(parsed[1], parsed[2], parsed[3]); break;
                        case "EXESCRIPT": exeScript(parsed[1], parsed[2]); break;
                        case "SWITCHLOADBALANCE": loadbalanceSwitch(parsed[1], Convert.ToInt32(parsed[2])); break;
                        case "LOADBALANCE": loadbalance(parsed[1]); break;
                        case "HELLO": hello(parsed[1]); break;
                        case "LBDUMP": loadBalanceDump(parsed[1]); break;
                        case "SLEEP": Thread.Sleep(Convert.ToInt32(parsed[1])); break;
                    }
                }
            }



        }


    }
}
