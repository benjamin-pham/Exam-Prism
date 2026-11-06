namespace Common.MultiTenancy;

public interface IConnectionStringProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

