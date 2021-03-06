﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using log4net;
using System.Collections;
using System.Runtime.Serialization.Formatters;
using System.Timers;
using System.Net.NetworkInformation;





public interface MyRemoteMetaDataInterface{

    string MetodoOla();

    //usados pelos client
    FileHandler open(string clientID, string filename);
    void close(string ClientID, FileHandler filehandler);
    FileHandler create(string clientID, string filename, int nbServers, int readQuorum, int writeQuorum);
    void confirmCreate(string clientID, string filename, Boolean created);
    FileHandler delete(string clientID, string filename);
    void confirmDelete(string clientID, FileHandler filehandler, Boolean deleted);
    FileHandler write(string clientID, FileHandler filehandler);
    void confirmWrite(string clientID, FileHandler filehander, Boolean wrote);
    void alive(); //usado pelo cliente para verificar que este meta-data está vivo
    
    void receiveAlive(string port); //usado pelo Data-Server para dizer que está vivo

    //usado pelo Puppet-Master
    void fail();
    void recover();
    void dump();
    void switchLoadBalancing(bool sw);
    void loadBalancing();
    void loadBalanceDump();

    //usado por outros Meta-Servers
    Boolean lockFile(string filename);
    Boolean unlockFile(string filename);
    void receiveUpdate(Dictionary<string, FileHandler>[] fileTable, Dictionary<string, DataServerInfo>[] newDataServersMap, bool loadBalancingUpdate);
    void sendUpdate(bool loadBalancingUpdate);
}





public class MyRemoteMetaDataObject : MarshalByRefObject, MyRemoteMetaDataInterface{

    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    /* Atributes */
    static string localPort;
    static string backPort;
    static string aMetaServerPort;
    static string bMetaServerPort;
    static int whoAmI; //0, 1 ou 2 to identify which Meta-Server it is 
    static List<string> dataServersPorts = new List<string>();
    static Boolean recovering = false;
    static System.Timers.Timer timeLoad = new System.Timers.Timer(10000); // timer que vai lançar o Loadbalancing de X em X milisegundos

    

    // Dict used for LoadBalancing and File Allocation
    public static Dictionary<string, DataServerInfo> dataServersMap = new Dictionary<string, DataServerInfo>();
    
    
    
    static Boolean isfailed;
    
    //Array of fileTables containing file Handlers
    public static Dictionary<string, FileHandler>[] fileTables = new Dictionary<string, FileHandler>[6];
    
    /* Constructors */
    public MyRemoteMetaDataObject()
    {
        isfailed = false;

        for (int i = 0; i < 6; i++)
            fileTables[i] = new Dictionary<string, FileHandler>();

        string path = Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%\\PADI-FS\\") + System.Diagnostics.Process.GetCurrentProcess().ProcessName + "-" + whoAmI;

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        Directory.SetCurrentDirectory(path);
        log.Info("Meta-Data Server is up!");
    }

    public MyRemoteMetaDataObject(string _localPort, string _aMetaServerPort, string _bMetaServerPort, string[] _dataServersPorts)
    {

        isfailed = false;
        localPort = _localPort;
        backPort = (Convert.ToInt32(localPort) + 2000).ToString();
        aMetaServerPort = _aMetaServerPort;
        bMetaServerPort = _bMetaServerPort;

        for (int i = 0; i < _dataServersPorts.Length; i++)
        {
            //dataServersPorts.Add(_dataServersPorts[i]);
            // adicionar os dataServers ao dataServerMap 
            DataServerInfo dsinfo = new DataServerInfo();
            dsinfo.MachineHeat = 0;
            dsinfo.dataServer = _dataServersPorts[i];
            dataServersMap.Add(_dataServersPorts[i], dsinfo);
        }

        for (int i = 0; i < 6; i++)
            fileTables[i] = new Dictionary<string, FileHandler>();

        if (Convert.ToInt32(localPort) < Convert.ToInt32(aMetaServerPort)
            && Convert.ToInt32(localPort) < Convert.ToInt32(bMetaServerPort))
        { whoAmI = 0; }
        else
        {
            if (Convert.ToInt32(localPort) > Convert.ToInt32(aMetaServerPort)
                && Convert.ToInt32(localPort) > Convert.ToInt32(bMetaServerPort))
            { whoAmI = 2; }

            else { whoAmI = 1; }
        }
        string path = Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%\\PADI-FS\\") + System.Diagnostics.Process.GetCurrentProcess().ProcessName + "-" + whoAmI;

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        Directory.SetCurrentDirectory(path);

        //load balancing timer
        timeLoad.AutoReset = true;
        timeLoad.Enabled = false; // by default loadbalancing is deactivated
        timeLoad.Elapsed += new ElapsedEventHandler(onTime);

        log.Info("Meta Server " + whoAmI + " is up!");
    }

    /* Loadbalancing switcher */
    public void switchLoadBalancing(bool sw)
    {
        timeLoad.Enabled = sw;

        if(sw)
            log.Info("[METASERVER: switchLoadBalancing]  LoadBalancing is now activated");
        else
            log.Info("[METASERVER: switchLoadBalancing]  LoadBalancing is now deactivated");
    }



    /* Event handler para o timer do loadbalancing */
    private void onTime(object source, ElapsedEventArgs e)
    {
        if (isfailed)
        {
            log.Info("[METASERVER: confirmCreate]    The server has is on 'fail'!");
            return;
        }

        MyRemoteMetaDataInterface[] mdi = new MyRemoteMetaDataInterface[2];
        mdi[0] = Utils.getRemoteMetaDataObj(aMetaServerPort);
        mdi[1] = Utils.getRemoteMetaDataObj(bMetaServerPort);
        
        // loadbalancing is made for the first (lower number) metaserver alive
        
        if(whoAmI == 0)
            loadBalancing();
        else if (whoAmI == 1)
        {
            try { mdi[0].alive(); }
            catch { loadBalancing(); }
        }
        else
        {
            try { mdi[0].alive(); }
            catch
            {
                try { mdi[1].alive();  }
                catch { loadBalancing(); }
            }
        }

    }

    /* Para a thread nunca se desligar */
    public override object InitializeLifetimeService(){ return null; }

    /* delegates */
    public delegate void prepareUpdateRemoteAsyncDelegate(Dictionary<string, FileHandler>[] newFileTable, Dictionary<string, DataServerInfo>[] newDataServersMap, bool loadBalancingUpdate);
    public delegate void askUpdateRemoteAsyncDelegate();
    public delegate void sendUpdateRemoveAsyncDelegate(bool loadBalancingUpdate);
    public delegate TransactionDTO TransferRemoteAsyncDelegate(TransactionDTO dto, string address);
    public delegate Boolean lockFileRemoveAsyncDelegate(string Filename);
    public delegate Boolean unlockFileRemoveAsyncDelegate(string Filename);



    /* Logic */
    public string MetodoOla(){ return "[META_SERVER]   Ola eu sou o MetaData Server!"; }

    public void alive()
    {
        if (!isfailed)
            log.Info("Yep, I'm alive =)");
        else
            throw new SocketException();
    }

    /************************************************************************
     *              Invoked Methods by Clients
     ************************************************************************/
    public FileHandler open(string clientID, string Filename)
    {
        /* 1. Is MetaServer Able to Respond  (Fail)
         * 2. Does the file Exist yet?
         * 3. Add to the File Handle, the clientID who has it opened
         * 4. Tells other Meta-Data Servers to update
         * 5. Returns FileHandler
         */

        FileHandler fh;
  
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: open]    The server has is on 'fail'!");
            return null;
        }

        //2.Does the file exist?
        if (!fileTables[Utils.whichMetaServer(Filename)].ContainsKey(Filename))
        {
            log.Info("[METASERVER: open]    The file doesn't exist yet (error)!");
            return null; //TODO return exception here! 
        }

        fh = fileTables[Utils.whichMetaServer(Filename)][Filename];

        //3. Add to the File Handle, the clientID who has it opened
        if (!fh.isOpen)
            fh.isOpen = true;
        fh.byWhom.Add(clientID);

        //4. Increment access count to this file
        fh.nFileAccess++;

        //5. Tells the other MetaServers to update
        sendUpdate(false);

        log.Info("[METASERVER: open]    Success)!");
        return fh;
    }

    public void close(string ClientID, FileHandler filehandlerReceived){
        /* 1. Is MetaServer Able to Respond  (Fail)
         * 2. Has this client a lock in this file? (If yes, denied close)
         * 3. Updates the respective File-Handle by removing this user from the byWhom list
         * 4. Tells other meta-data
         */

        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: close]    The server has is on 'fail'!");
            return;
        }

        FileHandler filehandler = fileTables[Utils.whichMetaServer(filehandlerReceived.filenameGlobal)][filehandlerReceived.filenameGlobal];

        //2. Has this client a lock in this file? (If yes, denied close)
        if (filehandler.isLocked)
        {
            log.Info("[METASERVER: close]    The File is locked!");
            return;
        }

        //3. Updates the respective File-Handle by removing this user from the byWhom list
        filehandler.byWhom.Remove(ClientID);

        //4. Increment access count to this file
        filehandler.nFileAccess++;

        //5. Put isOpen a false
        filehandler.isOpen = false;

        //6. Tells the other MetaServers to update
        sendUpdate(false);

        log.Info("[METASERVER: close]    Success)!");
    }


    public FileHandler create(string clientID, string filename, int nbServers, int readQuorum, int writeQuorum)
    {
        FileHandler fh;
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: create]    The server is on 'fail'!");
            return null;
        }

        //2. Does the file already exists? 
        if (fileTables[Utils.whichMetaServer(filename)].ContainsKey(filename))
        {
            log.Info("[METASERVER: create]    File already exists");
            return null; 
        }

        //3. Decide where the fill will be hosted
        //3.1 There are enought Data Servers in the system to meet the required replication?
        if (nbServers > dataServersMap.Count)
        {
            log.Info("[METASERVER: create]    There aren't enought Data Servers to meet the required replication");
            fh = new FileHandler(filename, 0, 0, new string[0], new string[0], readQuorum, writeQuorum, 1); // if there aren't enough data servers meta server sends always nbServer = 0 
            return fh;
        }
        
        //3.2 Select the first enougths DataServers to store the data - DUMB WAY
        string[] selectedDataServers = new string[nbServers];
        List<DataServerInfo> listOfDataServersAvailable = dataServersMap.Values.ToList();
        listOfDataServersAvailable.Sort((s1, s2) => s1.MachineHeat.CompareTo(s2.MachineHeat)); 
        for (int i = 0; i < nbServers; i++)
        {
            selectedDataServers[i] = listOfDataServersAvailable[i].dataServer;
        }

        // 3.3 Generate localfilenames
        string[] localNames = new string[nbServers];
        for (int i = 0; i < nbServers; i++)
            localNames[i] = Utils.genLocalName("m-" + whoAmI);

        //4. Create File-Handler 
        fh = new FileHandler(filename, 0, nbServers, selectedDataServers, localNames, readQuorum, writeQuorum, 1);
        
        //4.1 (new for load balancing) update DataServerInfo of dataServerMap
        foreach (String dsp in selectedDataServers)
        {
            dataServersMap[dsp].fileHandlers.Add(fh);
        }

        //5. Save the File-Handler
        fileTables[Utils.whichMetaServer(filename)].Add(filename, fh);

        //6. Lock File accross Meta-Data Servers
        lockFile(fh.filenameGlobal);        

        //7. Return File-Handler
        log.Info("[METASERVER: create]    Success!");
        
        return fh;
    }

    public void confirmCreate(string clientID, string filename, Boolean created) 
    {
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: confirmCreate]    The server has is on 'fail'!");
            return;
        }

        //2. Faz unlock ao ficheiro
        unlockFile(filename);
        log.Info("[METASERVER: confirmCreate]    Success!");

        // 3. Send Updated metadata table to others Metadata Servers
        sendUpdate(false);
    }



    public FileHandler delete(string clientID, string filename)
    {
        //1. Is the metaserver able to respond?
        if (isfailed)
        {
            log.Info("[METASERVER: delete]    The server has is on 'fail'!");
            return null;
        }

        //2. Does the file exist?
        if (!fileTables[Utils.whichMetaServer(filename)].ContainsKey(filename))
        {
            log.Info("[METASERVER: delete]    File does not exist!");
            return null; //TODO return exception here! 
        }

        //3. Get filehandler
        FileHandler fh = fileTables[Utils.whichMetaServer(filename)][filename];

        //4. O ficheiro está bloqueado?
        if (fileTables[Utils.whichMetaServer(filename)][filename].isLocked)
        {
            log.Info("[METASERVER: delete]    The File is locked!");
            return null;
        }

        //5.Faz lock ao ficheiro
        lockFile(fh.filenameGlobal);
        propagateLocks(fh.filenameGlobal);

        //6. Increment access count to this file
        fh.nFileAccess++;

        //7. Return the filehandler
        log.Info("[METASERVER: delete]    Success!");
        return fh;
    }

    public void confirmDelete(string clientID, FileHandler filehandler, Boolean deleted) 
    {
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: confirmDelete]    The server has is on 'fail'!");
            return;
        }

        //2. Apaga metadata associada ao ficheiro
        if (deleted == true)
        {
            fileTables[Utils.whichMetaServer(filehandler.filenameGlobal)].Remove(filehandler.filenameGlobal);
            log.Info("[METASERVER: confirmDelete]    Success!");
            // 3. Send Updated metadata table to others Metadata Servers
            sendUpdate(false);
            return;
        }

        unlockFile(filehandler.filenameGlobal);
        propagateUnlocks(filehandler.filenameGlobal);
        log.Info("[METASERVER: confirmDelete]    File was not deleted!");
    }



    public FileHandler write(string clientID, FileHandler filehandler)
    {
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: write]    The server has is on 'fail'!");
            return null;
        }

        //2. Does the file already exists? 
        if (!fileTables[Utils.whichMetaServer(filehandler.filenameGlobal)].ContainsKey(filehandler.filenameGlobal))
        {
            log.Info("[METASERVER: write]    The file doesn't exist yet (error)!");
            return null; //TODO return exception here! 
        }

        //3. O ficheiro está bloqueado?
        if (fileTables[Utils.whichMetaServer(filehandler.filenameGlobal)][filehandler.filenameGlobal].isLocked)
        {
            log.Info("[METASERVER: write]    The File is locked!");
            return null;
        }

        //4.Faz lock ao ficheiro
        lockFile(filehandler.filenameGlobal);
        propagateLocks(filehandler.filenameGlobal);

        //5. Increment access count to this file
        filehandler.nFileAccess++;

        //6. Devolve o filehandler ao cliente
        log.Info("[METASERVER: write]    Success!");
        return filehandler;
    }
        
    public void confirmWrite(string clientID, FileHandler filehandler, Boolean wrote) 
    {
        //1. Is MetaServer Able to Respond (Fail)
        if (isfailed)
        {
            log.Info("[METASERVER: confirmWrite]    The server has is on 'fail'!");
            return;
        }

        //2. Faz unlock ao ficheiro
        unlockFile(filehandler.filenameGlobal);
        propagateUnlocks(filehandler.filenameGlobal);
        log.Info("[METASERVER: confirmWrite]    Success!");

        //3. Updates file
        fileTables[Utils.whichMetaServer(filehandler.filenameGlobal)][filehandler.filenameGlobal].fileSize = filehandler.fileSize;
        fileTables[Utils.whichMetaServer(filehandler.filenameGlobal)][filehandler.filenameGlobal].version = filehandler.version;
        
        // 3. Send Updated metadata table to others Metadata Servers
        sendUpdate(false);
    }

    /************************************************************************
     *              Invoked Methods by Puppet-Master
     ************************************************************************/
    public void fail()
    {
      //  log.Info("My LOCAL PORT IS:" + localPort);
      
        TcpChannel channel = (TcpChannel) ChannelServices.GetChannel(localPort);
        ChannelServices.UnregisterChannel(channel);

        isfailed = true;
        log.Info("[METASERVER: fail]    Success!");
        return;
    }

    public void recover() {
        log.Info("Starting recovering procedure");

        if (isfailed)
        { /*
            for (int i = 0; i < 6; i++)
            {
                StreamReader sw = new StreamReader("backup-m" + whoAmI + "_table-" + i);
                string s = sw.ReadLine();
                while (!s.Equals(""))
                {
                    char p = '|';
                    string[] parsed = s.Split(p);

                    string filenameGlobal = parsed[0];
                    long fileSize = long.Parse(parsed[1]);
                    int nbServers = int.Parse(parsed[2]);
                    string[] dataPorts = parsed[3].Split(':');
                    int readQuorum = int.Parse(parsed[4]);
                    int writeQuorum = int.Parse(parsed[5]);
                    long nFileAccess = long.Parse(parsed[6]);


                    string[] dataFileNames = parsed[7].Split(':');
                    string[] localNames = new string[nbServers];
                    for (int j = 0; j < nbServers; j++)
                        localNames[j] = dataFileNames[i].Split('>')[1];


                    FileHandler fh = new FileHandler(filenameGlobal, fileSize, nbServers, dataPorts, localNames, readQuorum, writeQuorum, nFileAccess);
                    fh.version = long.Parse(parsed[8]);
                    fh.isOpen = bool.Parse(parsed[9]);

                    fileTables[i].Add(filenameGlobal, fh);

                    s = sw.ReadLine();
                }
                sw.Close();
            }*/

            BinaryServerFormatterSinkProvider provider = new BinaryServerFormatterSinkProvider();
            provider.TypeFilterLevel = TypeFilterLevel.Full;
            IDictionary props = new Hashtable();
            props["port"] = localPort;
            props["name"] = localPort;
            TcpChannel channel = new TcpChannel(props, null, provider);
            ChannelServices.RegisterChannel(channel, false);
            log.Info("Registered the channel again");           
        }
        isfailed = false;
        log.Info("Going to Request UPDATE on recovering");
        askForUpdate();

     
    }

    public void dump()
    {
        if (isfailed == true)
        {
            log.Info("[METASERVER: dump]    The server is already on fail!");
            isfailed = true;
        }



        string s = "Metadata stored in this Metadata Server:\n\n";
        for (int i = 0; i < 6; i++)
        {
            if (i % 2 == 0)
            {
                if (i == whoAmI)
                    s += "### I'm the primary for these files ####\n";
                else
                    s += "### I'm not the primary for these files, but have them stored ###\n";
            }
            foreach (FileHandler fh in fileTables[i].Values)
                s += fh.ToString() + "\n";

            if (i % 2 != 0)
                s += "\t-----------------------------\n\n";
        }

        log.Info("[METASERVER: dump]    Success!");
        log.Info(s);

        System.Console.WriteLine("I know this DATA-SERVERS:");
        foreach (string port in dataServersMap.Keys)
        {
            System.Console.WriteLine(port);
        }

        return;
    }

    /************************************************************************
     *              Invoked Methods by other Meta-Data Servers
     ************************************************************************/
    public Boolean lockFile(string Filename) {

        if (fileTables[Utils.whichMetaServer(Filename)][Filename].isLocked)
            log.Info("[METASERVER: lockFile]    The File was already locked! (not normal)");

        fileTables[Utils.whichMetaServer(Filename)][Filename].isLocked = true;

        log.Info("[METASERVER: lockFile]    File Locked Successful!");
        return true; 
    }

    public Boolean unlockFile(string Filename) {
        if (!fileTables[Utils.whichMetaServer(Filename)][Filename].isLocked)
            log.Info("[METASERVER: lockFile]    The File was not locked! (not normal)");

        fileTables[Utils.whichMetaServer(Filename)][Filename].isLocked = false;

        log.Info("[METASERVER: lockFile]    File unlocked Successful!");    
        return true; 
    }

    public void propagateLocks(string Filename)
    {
        //Propagate file lock.
        MyRemoteMetaDataInterface[] mdi = new MyRemoteMetaDataInterface[2];
        mdi[0] = Utils.getRemoteMetaDataObj(aMetaServerPort);
        mdi[1] = Utils.getRemoteMetaDataObj(bMetaServerPort);

        for (int i = 0; i < 2; i++)
        {

            //Invoque lockFile on remote MetaServer
            lockFileRemoveAsyncDelegate RemoteUpdate = new lockFileRemoveAsyncDelegate(mdi[i].lockFile);
            try
            {
                IAsyncResult RemAr = RemoteUpdate.BeginInvoke(Filename, null, null);
            }
            catch (SocketException e)
            {
                log.Info(" METASERVER:  lockFile:  Could not contact destination metaserver, no problem, it'll be updated when it recovers");
            }

            log.Info(" METASERVER:  lockFile:  Contacted metaserver to update on lockFile!");
        }
    }

    public void propagateUnlocks(string Filename)
    {
        //Propagate file lock.
        MyRemoteMetaDataInterface[] mdi = new MyRemoteMetaDataInterface[2];
        mdi[0] = Utils.getRemoteMetaDataObj(aMetaServerPort);
        mdi[1] = Utils.getRemoteMetaDataObj(bMetaServerPort);

        for (int i = 0; i < 2; i++)
        {

            //Invoque lockFile on remote MetaServer
            unlockFileRemoveAsyncDelegate RemoteUpdate = new unlockFileRemoveAsyncDelegate(mdi[i].unlockFile);
            try
            {
                IAsyncResult RemAr = RemoteUpdate.BeginInvoke(Filename, null, null);
            }
            catch
            {
                log.Info(" METASERVER:  unlockFile:  Could not contact destination metaserver, no problem, it'll be updated when it recovers");
            }

            log.Info(" METASERVER:  unlockFile:  Contacted metaserver to update on unlockFile!");
        }
    }

    public void sendUpdate(bool loadBalancingUpdate)
    {
        MyRemoteMetaDataInterface[] mdi = new MyRemoteMetaDataInterface[2];
        mdi[0] = Utils.getRemoteMetaDataObj(aMetaServerPort);
        mdi[1] = Utils.getRemoteMetaDataObj(bMetaServerPort);

        Dictionary<string, DataServerInfo>[] dataServersMap_toSend = new Dictionary<string, DataServerInfo>[1];
        dataServersMap_toSend[0] = dataServersMap;

        for (int i = 0; i < 2; i++)
        {
            prepareUpdateRemoteAsyncDelegate RemoteUpdate = new prepareUpdateRemoteAsyncDelegate(mdi[i].receiveUpdate);
            IAsyncResult RemAr = RemoteUpdate.BeginInvoke(fileTables, dataServersMap_toSend, loadBalancingUpdate, null, null);

            log.Info(" UPDATE SENDED::  Updated metadata table sended in background to others Metadata Servers");
        }

        // save the filetables to disk on a file

        for (int i = 0; i < 6; i++)
        {
            File.Create("backup-m" + whoAmI + "_table-" + i).Close();
            StreamWriter sw = new StreamWriter("backup-m" + whoAmI + "_table-" + i);
            string s = "";
            foreach (FileHandler fh in fileTables[i].Values)
            {
                s = (fh.filenameGlobal + "|" + fh.fileSize + "|" + fh.nbServers + "|");
                for (int j = 0; j < fh.nbServers; j++)
                {
                    s += fh.dataServersPorts[j];
                    if (j != fh.nbServers - 1)
                        s += ":";
                    else
                        s += "|";
                }
                s += (fh.readQuorum + "|" + fh.writeQuorum + "|" + fh.nFileAccess + "|");
                for (int j = 0; j < fh.nbServers; j++)
                {
                    s += fh.dataServersPorts[j] + ">" + fh.dataServersFiles[fh.dataServersPorts[j]];
                    if (j != fh.nbServers - 1)
                        s += ":";
                    else
                        s += "|";
                }
                s += fh.version + "|";
                s += fh.isOpen;
                sw.WriteLine(s);
            }

            sw.Close();
        }
    }

    public void receiveUpdate(Dictionary<string, FileHandler>[] newFileTable, Dictionary<string, DataServerInfo>[] newDataServersMap_received, bool loadBalancingUpdate)
    {
        log.Info("receiveUpdated call received");

        if (loadBalancingUpdate)
        {
            for (int i = 0; i < 6; i++)
            {
                fileTables[i] = newFileTable[i]; // ingles ver :)
            }
        }
        else
        {
            for (int i = 0; i < 6; i++)
                if (recovering)
                {
                    fileTables[i] = newFileTable[i];
                }
                else if (!(i == whoAmI * 2 || i == whoAmI * 2 + 1)) // dont update the files that it is responsible
                    fileTables[i] = newFileTable[i];

            recovering = false;
        }







        //Then reconstruct dataServerMap from filetables
        Dictionary<string, DataServerInfo> updatedDataServersMap = new Dictionary<string, DataServerInfo>();

        foreach (Dictionary<string, FileHandler> ftable in fileTables)
        {
            foreach (FileHandler fhandler in ftable.Values)
            {
                foreach (string dsport in fhandler.dataServersPorts)
                {
                    if (updatedDataServersMap.ContainsKey(dsport))
                    {
                        updatedDataServersMap[dsport].fileHandlers.Add(fhandler);
                    }
                    else
                    {
                        DataServerInfo dsinfo = new DataServerInfo();
                        dsinfo.dataServer = dsport;
                        dsinfo.fileHandlers.Add(fhandler);
                        updatedDataServersMap.Add(dsport, dsinfo);
                    }
                }
            }
        }
        dataServersMap = updatedDataServersMap;
        calculateMachineHeat();


        foreach (string dsport in newDataServersMap_received[0].Keys)
        {
            if (dataServersMap.ContainsKey(dsport)){
                continue;
            }else{
                DataServerInfo dsinfo = new DataServerInfo();
                dsinfo.MachineHeat = 0;
                dsinfo.dataServer = dsport;
                dataServersMap.Add(dsport, dsinfo);
            }
        }





 //       Dictionary<string, DataServerInfo> newDataServersMap = newDataServersMap_received[0];

//        foreach (string newPort in newDataServersMap.Keys) {
 //           if (!dataServersMap.ContainsKey(newPort)){
   //             dataServersPorts.Add(newPort);
    //        }
     //   }

        // save the filetables to disk on a file

        for (int i = 0; i < 6; i++)
        {
            File.Create("backup-m" + whoAmI + "_table-" + i).Close();
            StreamWriter sw = new StreamWriter("backup-m" + whoAmI + "_table-" + i);
            string s = "";
            foreach (FileHandler fh in fileTables[i].Values)
            {
                s = (fh.filenameGlobal + "|" + fh.fileSize + "|" + fh.nbServers + "|");
                for (int j = 0; j < fh.nbServers; j++)
                {
                    s += fh.dataServersPorts[j];
                    if (j != fh.nbServers - 1)
                        s += ":";
                    else
                        s += "|";
                }
                s += (fh.readQuorum + "|" + fh.writeQuorum + "|" + fh.nFileAccess + "|");
                for (int j = 0; j < fh.nbServers; j++)
                {
                    s += fh.dataServersPorts[j] + ">" + fh.dataServersFiles[fh.dataServersPorts[j]];
                    if (j != fh.nbServers - 1)
                        s += ":";
                    else
                        s += "|";
                }
                s += fh.version + "|";
                s += fh.isOpen;
                sw.WriteLine(s);
            }

            /*s = "";
            for (int k = 0; k < dataServersPorts.Count - 1; k++)
            {
                s += dataServersPorts[k] + ":";
            }
            sw.WriteLine(s);*/
            sw.Close();
        }

        log.Info(" UPDATE RECEIVED::  Updated metadata table sended in background to others Metadata Servers");
    }

    public void receiveAlive(string port)
    {
        //if (!dataServersPorts.Contains(port))
        //{
        //   dataServersPorts.Add(port);
        //}
        if (!dataServersMap.ContainsKey(port))
        {
            DataServerInfo dsinfo = new DataServerInfo();
            dsinfo.MachineHeat = 0;
            dsinfo.dataServer = port;
            dataServersMap.Add(port, dsinfo);
        }

    }

    public void askForUpdate()
    {
        MyRemoteMetaDataInterface[] mdi = new MyRemoteMetaDataInterface[2];
        mdi[0] = Utils.getRemoteMetaDataObj(aMetaServerPort);
        mdi[1] = Utils.getRemoteMetaDataObj(bMetaServerPort);

        recovering = true;

        for (int i = 0; i < 2; i++)
        {
            sendUpdateRemoveAsyncDelegate RemoteUpdate = new sendUpdateRemoveAsyncDelegate(mdi[i].sendUpdate);
            IAsyncResult RemAr = RemoteUpdate.BeginInvoke(false, null, null);

            log.Info(" REQUEST UPDATE SENDED::  Contacted other metaservers to ask for theis updates!");
        }


    }






    /************************************************************************************************
     * 
     *                                      LOAD BALANCING STUFF
     *
     ***********************************************************************************************/
    public void loadBalancing()
    {
        log.Info("LOAD BALANCING START");
        // Resume
        // 1. Calculate File-Heat and update File Handlers accordingly
        // 2. Put DataServerInfo objects in an array, Calculate Machine-Heat, sorted by ascending order
        // 3. Match High Half with Low Half (caution to check if it's even) use thermal Dissipation
        // 4. Calculate average heat, if goal is not reached, do cicle again ( to step 2 but before recalculate Machine-Heat again)
        // 5. Create struture <fileHandler, arrayOfnewDataServers>>
        // 6. Do the migrations
        // 7. Create dataServerMap again


        // 1 Calculate File-Heat and update File Handlers accordingly
        //log.Info("[LOADBALANCING]    Going to calculate File-Heats!");
        calculateFileHeat();
        //At this point, dataServerMap should have the DataServerInfo as well :)

        // 2. Calculate each Machine Heat and create an Array of DataServerInfo objects , sorted by ascending order
        calculateMachineHeat();

        Dictionary<string, DataServerInfo> dataServerMapClone = cloneDataServerMap();
        //Dictionary<string, DataServerInfo> dataServerMapClone = new Dictionary<string, DataServerInfo>(dataServersMap);
        //var dataServerMapClone = dataServersMap.ToDictionary(entry => entry.Key, entry => entry.Value);
        List<DataServerInfo> sortedDataServerInfo = new List<DataServerInfo>(dataServerMapClone.Values);

       // log.Info("[LOADBALANCING]    Machine Heat Before Load Balance:");

        log.Info("MAX Machine Heat: " + maxMachineHeat(sortedDataServerInfo));
        log.Info("AVG Machine Heat: " + averageMachineHeat(sortedDataServerInfo));
        

        int cicles = 0;

        do
        {
            sortedDataServerInfo.Sort((s1, s2) => s1.MachineHeat.CompareTo(s2.MachineHeat)); //SORTING LIKE A BOSS =D

          //  System.Console.WriteLine("SORTED DATA SERVER INFO");
            //foreach (DataServerInfo dsi in sortedDataServerInfo) { System.Console.WriteLine("DataServer - " + dsi.dataServer + "   Heat: " + dsi.MachineHeat); }
            
            cicles++;

            // 3. Match High Half with Low Half (caution to check if it's even) use thermal Dissipation
            int iterations = sortedDataServerInfo.Count / 2;
            int first_position = 0;
            int last_position = sortedDataServerInfo.Count - 1;

        //    log.Info("[LOADBALANCING]    Going to start the matching process!");
       //     System.Console.WriteLine("Number of Iterations: " + iterations);
            
            for (int i = 0; i < iterations; i++)
            {    
                thermalDissipation(sortedDataServerInfo[first_position + i], sortedDataServerInfo[last_position - i]);
            }

      //      log.Info("[LOADBALANCING]    Cicle Done!");
            
            // 4. Calculate average heat, if goal is not reached, do cicle again ( to step 2 but before recalculate Machine-Heat again)
            if (cicles > Constants.LOADBALANCER_CICLE_LIMIT)
                break;
            
    //        log.Info("[LOADBALANCING]    Going to calculate machine heats!");
            calculateMachineHeatClone(dataServerMapClone);
            /*
            foreach (DataServerInfo dsi in sortedDataServerInfo)
            {
                System.Console.WriteLine("Data Server: " + dsi.dataServer + "   Heat: " + dsi.MachineHeat);
            }
  */
        } while ( !(maxMachineHeat(sortedDataServerInfo) <= (averageMachineHeat(sortedDataServerInfo) * Constants.LOADBALANCER_THRESHOLD)));


       
        /*log.Info("[LOADBALANCING]    Machine Heat After Load Balance:");

        foreach (DataServerInfo dsi in sortedDataServerInfo)
        {
            System.Console.WriteLine("Data Server: " + dsi.dataServer + "   Heat: " + dsi.MachineHeat);
        }
        */
        // 5. Create struture <fileHandler, arrayOfnextDataServers>>
       // log.Info("[LOADBALANCING]    Going to create migrationDataStructure!");
        Dictionary<FileHandler, List<string>> migrationData = createMigrationDataStructure(sortedDataServerInfo);

       // foreach (FileHandler fh in migrationData.Keys) {
           // System.Console.WriteLine("File: " + fh.filenameGlobal + ":");
            /*
            foreach (string dataserver in migrationData[fh]) {
                System.Console.WriteLine("DataServer: " + dataserver);
            }*/
        //}
        
        // 6. Do the migrations
        List<FileHandler> updatedFileHandlers = new List<FileHandler>();
        foreach (FileHandler fhandler in migrationData.Keys)
        {
            List<string> nextDataServers = migrationData[fhandler]; //nextDataServers Contem aqueles que são novos e aqueles que não foram alteradoss

            /*
            log.Info("############ " + fhandler.filenameGlobal);
            log.Info("LAST");
            foreach (string last in fhandler.dataServersPorts)
            {
                log.Info(last);
            }
            log.Info("NEXT");
            foreach (string next in nextDataServers)
            {
                log.Info(next);
            }*/


            //log.Info("STEP 5");
            List<string> oldDataServers = checkOld(fhandler, nextDataServers);
            /*log.Info("OLD");
            foreach (string old in oldDataServers)
            {
                log.Info(old);
            }*/
            List<string> newDataServers = checkNew(fhandler, nextDataServers);
            /*log.Info("NEW");
            foreach (string nuevo in newDataServers)
            {
                log.Info(nuevo);
            }
            */
            List<string> sameDataServers = checkSame(fhandler, nextDataServers);
            /*log.Info("SAME");
            foreach (string same in sameDataServers)
            {
                log.Info(same);
            }
            log.Info("############################################################");
            */

            migrate(oldDataServers, newDataServers, sameDataServers, fhandler);
            updatedFileHandlers.Add(fhandler);
        }
        log.Info("LOAD BALANCING FINISH");
        
        // 7. Update dataServerMap again
        updateDataServerMap(updatedFileHandlers);
        calculateMachineHeat();
        sendUpdate(true); // Update everyone =D
        
    }


    //Calculate number of total accesses
    public long TotalAccesses() {

        long total = 0;

        for (int i = 0; i < 6; i++) {
            foreach (FileHandler fh in fileTables[i].Values) {
                total += fh.nFileAccess;
            }
        }

        return total;
    }

    // To be used in 1.
    public void calculateFileHeat()
    {
        long numberOfTotalAccesses = TotalAccesses();
        for (int i = 0; i < 6; i++)
        {
            foreach (FileHandler fh in fileTables[i].Values)
            {
                long fileSize = fh.fileSize;
                if (fileSize <= 0) fileSize = 1; //Avoid Log(0)
                fh.heat = (double) (fh.nFileAccess / (double) numberOfTotalAccesses) + (double) Math.Log10(fileSize);
            }
        }

    }

    // To be used in 2.
    public void calculateMachineHeat()
    {
        foreach (DataServerInfo dsi in dataServersMap.Values) {
            double sum = 0;
        
            foreach (FileHandler fh in dsi.fileHandlers) {
                sum += fh.heat;
            }
            dsi.MachineHeat = sum;
        }
        return;
    }

    // To be used in 2
    public void calculateMachineHeatClone(Dictionary<string, DataServerInfo> dataServerMap)
    {
        foreach (DataServerInfo dsi in dataServerMap.Values)
        {
            double sum = 0;

            foreach (FileHandler fh in dsi.fileHandlers)
            {
                sum += fh.heat;
            }
            dsi.MachineHeat = sum;
        }
    }

    // To be used in 3
    public void thermalDissipation(DataServerInfo coldest, DataServerInfo hotest)
    {
        int index = 0;

        if (hotest.fileHandlers.Count <= 0)
            return;     //Sossega a bicharola!

        while (index < hotest.fileHandlers.Count){
            /*foreach (FileHandler f in coldest.fileHandlers)
            {
                log.Info("COLDEST: " + f.filenameGlobal);
            }
            foreach (FileHandler f in hotest.fileHandlers)
            {
                log.Info("HOTEST: " + f.filenameGlobal);
            }
            */
            if(hotest.fileHandlers[index].isOpen){
                index++;
                continue;
            }

            bool alreadyHasIt = false;
            foreach (FileHandler fhcold in coldest.fileHandlers){
                if (fhcold.filenameGlobal == hotest.fileHandlers[index].filenameGlobal)
                {
                    alreadyHasIt = true;
                }
            }
            if (alreadyHasIt)
            {
                index++;
                continue;
            }

            /*
            if (coldest.fileHandlers.Contains(hotest.fileHandlers[index]) || hotest.fileHandlers[index].isOpen){
                log.Info("ESTOU A DIZER QUE O COLDEST contem " + hotest.fileHandlers[index].filenameGlobal);
                foreach (FileHandler f in coldest.fileHandlers)
                {
                    log.Info("ENQUANTO ELE CONTEM: " + f.filenameGlobal);
                }
                index++;
                continue;
            }
            */
          //  log.Info("I'm going to move from hotest: " + hotest.fileHandlers[index].filenameGlobal);

            coldest.fileHandlers.Add(hotest.fileHandlers[index]);
            hotest.fileHandlers.Remove(hotest.fileHandlers[index]);
            break;
        }

    }

    // To be used in 4.
    public double averageMachineHeat(List<DataServerInfo> allDSI)
    {
        double sum = 0;

        foreach (DataServerInfo dsi in allDSI) {
            sum += dsi.MachineHeat;
        }

        return (double) ((double)sum/(double)dataServersMap.Count);
    }

    // To be used in 4.
    public double maxMachineHeat(List<DataServerInfo> allDSI)
    {
        double max = 0;

        foreach (DataServerInfo dsi in allDSI) {
            if (dsi.MachineHeat > max)
                max = dsi.MachineHeat;
        }
        
        return max;
    }
   
    // To be used in 4.
    public double minMachineHeat(List<DataServerInfo> allDSI)
    {
        double min = long.MaxValue;

        foreach (DataServerInfo dsi in allDSI)
        {
            if (dsi.MachineHeat < min)
                min = dsi.MachineHeat;
        }

        return min;
    }

    // To be used in 5.
    public Dictionary<FileHandler, List<string>> createMigrationDataStructure(List<DataServerInfo> dsi_list) {
        
        Dictionary<FileHandler, List<string>> ret = new Dictionary<FileHandler, List<string>>();
        Dictionary<string, List<string>> temp = new Dictionary<string, List<string>>();
        List<string> dataservers;
        List<FileHandler> allFilehandlers = new List<FileHandler>();

        foreach (DataServerInfo dsi in dsi_list) {
            foreach (FileHandler fh in dsi.fileHandlers) {
                if (temp.Keys.Contains(fh.filenameGlobal))
                {
                    temp[fh.filenameGlobal].Add(dsi.dataServer);
                }
                else {
                    dataservers = new List<string>();
                    dataservers.Add(dsi.dataServer);
                    temp.Add(fh.filenameGlobal, dataservers);
                }
            }
        }
        foreach (string filename in temp.Keys) {
            foreach (DataServerInfo dsi in dsi_list) {
                foreach (FileHandler fh in dsi.fileHandlers) {
                    allFilehandlers.Add(fh);
                }
            }
        }

        foreach (string fname in temp.Keys) {
            foreach (FileHandler fh in allFilehandlers) {
                if (fname == fh.filenameGlobal) {
                    ret.Add(fh, temp[fname]);
                    break;
                }
            }
        }
        return ret;
    }

    public Dictionary<string, DataServerInfo> cloneDataServerMap() { 
        Dictionary<string, DataServerInfo> ret = new Dictionary<string,DataServerInfo>();
        List<FileHandler> fhlist;
        foreach (string key in dataServersMap.Keys) {
            fhlist = new List<FileHandler>();
            foreach (FileHandler fh in dataServersMap[key].fileHandlers) {
                string[] localnames = new string[fh.dataServersFiles.Values.Count];
                fh.dataServersFiles.Values.CopyTo(localnames,0);
                FileHandler newFh = new FileHandler(fh.filenameGlobal, fh.fileSize, fh.nbServers, fh.dataServersPorts, localnames, fh.readQuorum, fh.writeQuorum, fh.nFileAccess);
                List<string> byWhom = new List<string>(fh.byWhom);
                newFh.isOpen = fh.isOpen;
                newFh.byWhom = byWhom;
                newFh.heat = fh.heat;
                newFh.version = fh.version;
                newFh.isLocked = fh.isLocked;
                newFh.byWho = fh.byWho;
                fhlist.Add(newFh);
            }
            ret.Add(key, new DataServerInfo(dataServersMap[key].MachineHeat, fhlist, dataServersMap[key].dataServer));  
        }
        return ret;
    }

    // To be used in 6.
    public List<string> checkOld(FileHandler fhandler, List<string> nextDataServers)
    {
        List<string> oldList = new List<string>();
        foreach (string ds in fhandler.dataServersPorts)
        {
            if (nextDataServers.Contains(ds))
            {
                continue;
            }
            else
            {
                oldList.Add(ds);
            }
        }
        return oldList;
    }

    // To be used in 6.
    public List<string> checkNew(FileHandler fhandler, List<string> nextDataServers)
    {
        List<string> newList = new List<string>();
        foreach (string ds in nextDataServers)
        {
            if (fhandler.dataServersPorts.Contains(ds))
            {
                continue;
            }
            else
            {
                newList.Add(ds);
            }
        }
        return newList;
    }

    // To be used in 6.
    public List<string> checkSame(FileHandler fhandler, List<string> nextDataServers)
    {
        List<string> sameList = new List<string>();
        foreach (string ds in fhandler.dataServersPorts)
        {
            if (nextDataServers.Contains(ds))
            {
                sameList.Add(ds);
            }
            else
            {
                continue;   
            }
        }
        return sameList;
    }

    // To be used in 6.
    public void migrate(List<string> oldDataServers, List<string> newDataServers,List<string> sameDataServers,  FileHandler fhandler)
    {
        //1. Ciclo para fazer todas as transfers
        //1.1 Fazer o migrate, se resultar bem cool, se não actualizar a lista nova e colocar o antigo
        //1.2 Actualizar no File Handler a referência do nome para o data server
        //2 Actualizar a lista de data servers (same+new)
        /*
        log.Info("ACTUAL SERVER PORTS for file: " + fhandler.filenameGlobal);
        foreach(string dp in fhandler.dataServersPorts){
            log.Info(dp);
        }
        log.Info("--");

        log.Info("OLD SERVER PORTS for file: " + fhandler.filenameGlobal);
        foreach (string dp in oldDataServers)
        {
            log.Info(dp);
        }
        log.Info("--");

        log.Info("NEW SERVER PORTS for file: " + fhandler.filenameGlobal);
        foreach (string dp in newDataServers)
        {
            log.Info(dp);
        }
        log.Info("--");

        log.Info("SAME SERVER PORTS for file: " + fhandler.filenameGlobal);
        foreach (string dp in sameDataServers)
        {
            log.Info(dp);
        }
        log.Info("--");
        */

        int dscount = newDataServers.Count;
       
        for (int i = 0; i < dscount; i++)
        {
            if (transfer(oldDataServers[i], newDataServers[i], fhandler) != true)
            {
                newDataServers[i] = oldDataServers[i];
            }
            else {
                fhandler.dataServersFiles.Add(newDataServers[i], fhandler.dataServersFiles[oldDataServers[i]]); 
                fhandler.dataServersFiles.Remove(oldDataServers[i]);
            }
        }
        List<string> result = new List<string>(newDataServers);
        result.AddRange(sameDataServers);

        fhandler.dataServersPorts = result.Distinct().ToArray();
        /*
        log.Info("NEW SERVER PORTS for file: " + fhandler.filenameGlobal);
        foreach (string dp in fhandler.dataServersPorts)
        {
            log.Info(dp);
        }
        log.Info("--");
        */
        return;
    }

    public Boolean transfer(string oldDS, string newDS, FileHandler fh) {

        TransactionDTO dto = new TransactionDTO(Utils.generateTransactionID(), "metaserver", fh.dataServersFiles[oldDS]);

        MyRemoteDataInterface rdi = Utils.getRemoteDataServerObj(oldDS);
        return rdi.transferFile(dto, "tcp://localhost:" + newDS + "/sdasdssd").success;
    }


    // To be used in 7.
    public void updateDataServerMap(List<FileHandler> updatedFhandlerList)
    {
        //Actually we are going to use FileTables for this update =)
        
        foreach(FileHandler fh in updatedFhandlerList){
            FileHandler FileHandlerToUpdate = fileTables[Utils.whichMetaServer(fh.filenameGlobal)][fh.filenameGlobal];
            FileHandlerToUpdate.dataServersPorts = fh.dataServersPorts;
            FileHandlerToUpdate.dataServersFiles = fh.dataServersFiles;
        }
        
        //Then reconstruct dataServerMap
        Dictionary<string, DataServerInfo> updatedDataServersMap = new Dictionary<string, DataServerInfo>();
        
        foreach (Dictionary<string, FileHandler> ftable in fileTables)
        {
            foreach (FileHandler fhandler in ftable.Values)
            {
                foreach (string dsport in fhandler.dataServersPorts)
                {
                    if(updatedDataServersMap.ContainsKey(dsport))
                    {
                        updatedDataServersMap[dsport].fileHandlers.Add(fhandler);
                    }
                    else
                    {
                        DataServerInfo dsinfo = new DataServerInfo();
                        dsinfo.dataServer = dsport;
                        dsinfo.fileHandlers.Add(fhandler);
                        updatedDataServersMap.Add(dsport,dsinfo);
                    }
                }
            }
        }
        dataServersMap = updatedDataServersMap;
        
    }


    // LoadBalance Results
    public void loadBalanceDump() {

        System.Console.WriteLine();
        System.Console.WriteLine("_______________[LOAD_BALANCE_STATS]______________");
        System.Console.WriteLine();

        int LINES = 14;

        calculateMachineHeat();
        List<DataServerInfo> allDSI = new List<DataServerInfo>(dataServersMap.Values);

        double maxheat = maxMachineHeat(allDSI);
        int average = ((int)averageMachineHeat(allDSI) / (int)maxheat) * (LINES);


        string top_c =   "_____ ";
        string torso_c = "|   | ";
        string empty_c = "      ";

        char[][] chart = new char[LINES][];

        int top;
        string s;
        // Print chart
        System.Console.WriteLine();
        for (int current_line = 0; current_line < LINES; current_line++)
        {
            if ((LINES - average) == current_line)
            {
                s = "AVG___";
            }
            else
            {
                s = "      ";
            }

            foreach (DataServerInfo dsi in allDSI) {
           
                top = ((int)dsi.MachineHeat / (int)maxheat) * (LINES); //[0 , 19]
                if ((LINES - top) == current_line)
                {
                    s += top_c;
                }
                else if (current_line < (LINES - top))
                {
                    s += empty_c;
                }
                else if (current_line > (LINES - top))
                {
                    s += torso_c;
                }
            }
            System.Console.WriteLine(s);

        }
        s = "       ";
        foreach (DataServerInfo dsi in allDSI)
        {
            s += "------";
        }
        System.Console.WriteLine(s);

        s = "       ";
        foreach (DataServerInfo dsi in allDSI) {
            s +=  dsi.dataServer + " ";
        }
        System.Console.WriteLine(s);
        System.Console.WriteLine();
    }
}
