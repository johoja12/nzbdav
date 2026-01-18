using System;
using System.Security.Cryptography;
using System.Text;

var name = "7d9472c8-0699-ca53-b5f5-46857103f8d8";
var hash = SHA1.HashData(Encoding.UTF8.GetBytes(name));
Console.WriteLine(BitConverter.ToString(hash).Replace("-", " "));
