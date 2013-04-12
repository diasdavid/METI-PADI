﻿using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

public interface MyRemoteDataInterface
{
    //Metodos auxiliares
    string MetodoOla();
    void showFilesinMutation();

    //usado pelo cliente
    TransactionDTO prepareWrite(TransactionDTO dto);                                    //DONE
    TransactionDTO commitWrite(TransactionDTO dto);                                     //DONE
    byte[] read(string local_file_name, int semantic);                                  //SEMI-DONE (ignora a semantica)
    TransactionDTO prepareCreate(TransactionDTO dto);                                   //DONE
    TransactionDTO commitCreate(TransactionDTO dto);                                    //DONE
    TransactionDTO prepareDelete(TransactionDTO dto);                                   //DONE
    TransactionDTO commitDelete(TransactionDTO dto);                                    //DONE

    //usado pelo meta-server
    Boolean transferFile(string filename, string address);                              //TODO (after checkpoint)

    //usado pelo data-server
    Boolean receiveFile(string filename, byte[] file);                                  //TODO (after checkpoint)

    //usado pelo puppet-master
    void freeze();                                                                   //DONE
    void unfreeze();                                                                 //DONE
    void fail();                                                                     //DONE
    void recover();                                                                  //DONE
}

public class MutationListItem {
    public string filename;
    public string clientID;
    public byte[] byte_array;

    public MutationListItem() { }

    public MutationListItem(string name, string ID, byte[] b_array) {
        filename = name;
        clientID = ID;
        byte_array = b_array;
    }
}



public class MyRemoteDataObject : MarshalByRefObject, MyRemoteDataInterface
{
    private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
   
    //Semanticas
    public const int DEFAULT = 1;
    public const int MONOTONIC = 2;
    
    //Estados do servidor
    public static Boolean isfailed = false;
    public static Boolean isfrozen = false;

    //Lista de Ficheiros Mutantes
    public static List<MutationListItem> mutationList;

    //Construtor
    public MyRemoteDataObject(int dataServerNumber)
    {
        mutationList = new List<MutationListItem>();

        string path = Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%\\PADI-FS\\") + System.Diagnostics.Process.GetCurrentProcess().ProcessName + "-" + dataServerNumber;

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        
        Directory.SetCurrentDirectory(path);
        log.Info("Data Server is up!");
        log.Info(Directory.GetCurrentDirectory());
    }

    //Metodos auxiliares
    public void showFilesinMutation() {
        foreach (MutationListItem item in mutationList){
            Console.WriteLine(item.clientID + " " + item.filename + "Content: " + System.Text.Encoding.Default.GetString(item.byte_array));
        }
    }

    public override object InitializeLifetimeService() { return null;  }

    // Communication Test Method
    public string MetodoOla()  { return "[DATA_SERVER]   Ola eu sou o Data Server!";  }


    /************************************************************************
     *          
     *                           Remote Methods
     *              
     ************************************************************************/


    //Usado pelo cliente
    public TransactionDTO prepareWrite(TransactionDTO dto) {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);
        //showFilesinMutation();

        if (isfailed == true){
            log.Info("WRITE :: PrepareWrite : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true){
            log.Info("WRITE :: PrepareWrite : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        //Verifica a existencia do ficheiro
        if (!File.Exists(dto.filenameForDataServer))
        {
            log.Info("WRITE :: PrepareWrite : File(LocalName): " + dto.filenameForDataServer + " does not exist");
            newDTO.success = false;
            return newDTO;
        }

        //Verifica se o ficheiro ja esta a ser alterado
        if (mutationList.Find(f => f.filename == dto.filenameForDataServer) != null){
            log.Info("WRITE :: PrepareWrite : File(LocalName): " + dto.filenameForDataServer + " does not exist");
            newDTO.success = false;
            return newDTO;
        }

        MutationListItem mutationEntry = new MutationListItem(dto.filenameForDataServer, dto.clientID, dto.filecontent);
        mutationList.Add(mutationEntry);

        log.Info("WRITE :: PrepareWrite : Operation complete for File(LocalName): " + dto.filenameForDataServer);
        newDTO.success = true;
        return newDTO;
    }

    public TransactionDTO commitWrite(TransactionDTO dto) {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);
        
        if (isfailed == true){
            log.Info("WRITE :: CommitWrite : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true){
            log.Info("WRITE :: CommitWrite : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        MutationListItem item = mutationList.Find(i => i.filename == dto.filenameForDataServer && i.clientID == dto.clientID);
        if (item == null){
            log.Info("WRITE :: CommitWrite : Client: " + dto.clientID + " is trying to Commit the file(localname): " + dto.filenameForDataServer +" without preparing");
            newDTO.success = false;
            return newDTO;
        }

        mutationList.Remove(item);
        File.WriteAllBytes(item.filename, item.byte_array);
        log.Info("WRITE :: CommiteWrite : Operation complete from Client: " + dto.clientID + " file(localname): " + dto.filenameForDataServer);
        newDTO.success = true;
        return newDTO;
    }






    public byte[] read(string local_file_name, int semantic)
    {

        // this method is limited to 2^32 byte files (4.2 GB)
        byte[] bytes = null;

        if (isfailed == true)
        {
            Console.WriteLine("[DATA_SERVER: read]    The server has failed!");
            return bytes;
        }

        if (isfrozen == true)
        {
            Console.WriteLine("[DATA_SERVER: read]    The server is frozen!");

            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        FileStream fs;

        fs = File.OpenRead(local_file_name);
        bytes = new byte[fs.Length];
        fs.Read(bytes, 0, Convert.ToInt32(fs.Length));
        fs.Close();

        Console.WriteLine("[DATA_SERVER: read]    Success!");
        return bytes;
    }






    public TransactionDTO prepareCreate(TransactionDTO dto) {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);

        if (isfailed == true)
        {
            log.Info("CREATE :: PrepareCreate : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true)
        {
            log.Info("CREATE :: PrepareCreate : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        //Verifica a existencia do ficheiro
        if (File.Exists(dto.filenameForDataServer))
        {
            log.Info("CREATE :: PrepareCreate : The file requested to create already exists");
            newDTO.success = false;
            return newDTO;
        }

        //Verifica se o ficheiro ja esta a ser alterado
        if (mutationList.Find(f => f.filename == dto.filenameForDataServer) != null)
        {
            log.Info("CREATE :: PrepareCreate : The file requested to create is already being manipulated by other process");
            newDTO.success = false;
            return newDTO;
        }

        MutationListItem mutationEntry = new MutationListItem(dto.filenameForDataServer, dto.clientID, null);
        mutationList.Add(mutationEntry);

        log.Info("CREATE :: PrepareCreate : Operation Complete");
        newDTO.success = true;
        return newDTO;
    }

    public TransactionDTO commitCreate(TransactionDTO dto)
    {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);

     
        if (isfailed == true){
            log.Info("CREATE :: CommitCreate : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true){
            log.Info("CREATE :: CommitCreate : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        MutationListItem item = mutationList.Find(i => i.filename == dto.filenameForDataServer && i.clientID == dto.clientID);
        if (item == null){
            log.Info("CREATE :: CommitCreate : There was no request before for 'Prepare' can't fast forward");          
            newDTO.success = false;
            return newDTO;
        }

        mutationList.Remove(item);
        File.Create(dto.filenameForDataServer).Close();

        log.Info("CREATE :: CommitCreate : Operation complete by Client: " + dto.clientID + " for file: " + dto.filenameForDataServer);
        newDTO.success = true;
        return newDTO;
    }










    public TransactionDTO prepareDelete(TransactionDTO dto) {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);

        if (isfailed == true)
        {
            log.Info("DELETE :: PrepareDelete : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true)
        {
            log.Info("DELETE :: PrepareDelete : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        // Verifica a existencia do ficheiro
        if (!File.Exists(dto.filenameForDataServer))
        {
            log.Info("DELETE :: PrepareDelete : The requested file in this Data-Server does not exist, NAME: " + dto.filenameForDataServer);
            newDTO.success = false;
            return newDTO;
        }

        // Verifica se o ficheiro já esta a ser alterado
        if (mutationList.Find(f => f.filename == dto.filenameForDataServer) != null)
        {
            log.Info("DELETE :: PrepareDelete : The requested file in this Data-Server is being manipulated by another process");
            newDTO.success = false;
            return newDTO;
        }

        MutationListItem mutationEntry = new MutationListItem(dto.filenameForDataServer, dto.clientID, null);
        mutationList.Add(mutationEntry);

        log.Info("DELETE :: PrepareDelete : Operation Complete");
        newDTO.success = true;
        return newDTO;
    }

    public TransactionDTO commitDelete(TransactionDTO dto) {
        TransactionDTO newDTO = new TransactionDTO(dto.transactionID, dto.clientID, dto.filenameForDataServer);

        if (isfailed == true)
        {
            log.Info("DELETE :: CommitDelete : This server is 'failed' can't comply with the request");
            newDTO.success = false;
            return newDTO;
        }

        if (isfrozen == true)
        {
            log.Info("DELETE :: CommitDelete : This server is 'frozen' can't comply with the request right now");
            Monitor.Enter(mutationList);
            Monitor.Wait(mutationList);
            Monitor.Exit(mutationList);
        }

        MutationListItem item = mutationList.Find(i => i.filename == dto.filenameForDataServer && i.clientID == dto.clientID);
        if (item == null)
        {
            log.Info("DELETE :: CommitDelete : Client: " + dto.clientID + " is trying to commit without prepare");
            newDTO.success = false;
            return newDTO;
        }

        mutationList.Remove(item);
        File.GetAccessControl(dto.filenameForDataServer);
        File.Delete(dto.filenameForDataServer);

        log.Info("DELETE :: CommitDelete : Operation Complete");
        newDTO.success = true;
        return newDTO;
    }




    //usado pelo meta-server
    public Boolean transferFile(string filename, string address) { return true; }

    //usado pelo data-server
    public Boolean receiveFile(string filename, byte[] file) { return true; }







    //usado pelo puppet-master
    public void freeze() {

        if (isfailed == true)
        {
            Console.WriteLine("[DATA_SERVER: freeze]    Cannot freeze during server failure!");
            return;
        }
        isfrozen = true;
        Console.WriteLine("[DATA_SERVER: freeze]    Success!");
        return; 
    }

    public void unfreeze() {

        if (isfrozen == false){
            Console.WriteLine("[DATA_SERVER: unfreeze]    The server was not frozen!");
            return;
        }

        isfrozen = false;
        if (!Monitor.IsEntered(mutationList))
            Monitor.Enter(mutationList);

        Monitor.PulseAll(mutationList);
        Monitor.Exit(mutationList);
        Console.WriteLine("[DATA_SERVER: unfreeze]    Sucess!");
        return;
    }

    public void fail() {

        if (isfrozen == true){
            Console.WriteLine("[DATA_SERVER: fail]    Cannot fail while server is frozen!");
        }

        isfailed = true;
        Console.WriteLine("[DATA_SERVER: faile]    Success!");
        return; 
    }

    public void recover() {
        if (isfailed == false)
        {
            Console.WriteLine("[DATA_SERVER: recover]    The server was not failed!");
            return;
        }
        isfailed = false;
        Console.WriteLine("[DATA_SERVER: recover]    Success!");
        return;
    }
}