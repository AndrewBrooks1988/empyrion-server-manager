// Update signing for Empyrion Server Manager releases (like Tauri's updater key, but plain ECDSA P-256).
//
//   genkey <private-key.pem>             create a key pair; prints the PUBLIC key to paste into UpdateService.PublicKeyPem
//   sign   <file> <private-key.pem>      writes <file>.sig (base64 ECDSA-SHA256 signature, IEEE P1363 format)
//   verify <file> <file.sig> <public.pem> checks a signature
//
// Keep the private key OUT of the repository (default: %USERPROFILE%\.empyrion-server-manager\update-signing-key.pem).
using System.Security.Cryptography;

if (args.Length < 2) { Console.Error.WriteLine("usage: genkey <key.pem> | sign <file> <key.pem> | verify <file> <sig> <pub.pem>"); return 2; }

switch (args[0])
{
    case "genkey":
    {
        if (File.Exists(args[1])) { Console.Error.WriteLine($"{args[1]} already exists - refusing to overwrite a signing key."); return 1; }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        return 0;
    }
    case "sign":
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(args[2]));
        var sig = key.SignData(File.ReadAllBytes(args[1]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllText(args[1] + ".sig", Convert.ToBase64String(sig));
        Console.WriteLine($"signed {Path.GetFileName(args[1])}");
        return 0;
    }
    case "verify":
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(args[3]));
        var ok = key.VerifyData(File.ReadAllBytes(args[1]), Convert.FromBase64String(File.ReadAllText(args[2]).Trim()),
                                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine(ok ? "valid" : "INVALID");
        return ok ? 0 : 1;
    }
    default:
        Console.Error.WriteLine("unknown command"); return 2;
}
