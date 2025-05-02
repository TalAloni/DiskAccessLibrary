DiskAccessLibrary:
===================
DiskAccessLibrary is an open-source C# library allowing access to physical and virtual disks (IMG/VHD/VMDK) including reading and writing various on-disk structutes (MBR/GPT, Logical Disk Manager Database) and filesystems (NTFS).  

##### Q: What this library can do?  
1. Create IMG/VHD/VMDK virtual hard drives.  
2. Read and write from IMG/VHD/VMDK virtual hard drives.  
3. Read and modify MBR and GPT partition table.  
4. Read and modify Windows Logical Disk manager database.  
5. Read files from NTFS formatted volumes.
6. Write files to NTFS formatted volums. (The code was not updated to reflect changes in Windows Vista and later and was only validated against Windows XP and Windows Server 2003)
7. Create NTFS formatted volumes. (The code was not updated to reflect changes in Windows Vista and later and was only validated against Windows XP and Windows Server 2003)

##### Warnings:  
1. The software may contain bugs and/or limitations that may result in data loss, backup your critical data before using it.  
I take no responsibility for any data loss that may occur.  

2. There are no official specifications for the LDM database.  
the structure has been reverse engineered ( https://flatcap.github.io/linux-ntfs/ldm/ ) and while I believe most of the information is accurate, there are some unknowns.  

3. There are no official specifications for the NTFS file system.  

##### Programs using DiskAccessLibrary:  
[Dynamic Disk Partitioner](https://github.com/TalAloni/DynamicDiskPartitioner)  
[iSCSI Console](https://github.com/TalAloni/iSCSIConsole)  
[Hard Disk Validator](https://github.com/TalAloni/HardDiskValidator)  
[Raw Disk Copier](https://github.com/TalAloni/RawDiskCopier)  
[VmdkZeroFree](https://github.com/TalAloni/VmdkZeroFree)  

Contact:
========
If you have any question, feel free to contact me.  
Tal Aloni <tal.aloni.il@gmail.com>
