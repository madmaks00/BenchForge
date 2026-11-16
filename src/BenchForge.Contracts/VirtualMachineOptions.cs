using System.Text.RegularExpressions;

namespace BenchForge.Contracts;

public sealed record VirtualMachineOptions
{
    public string Template { get; init; } = "benchforge-base-v1";
    public int Cpus { get; init; } = 4;
    public int MemoryMb { get; init; } = 3072;
    public int DiskGb { get; init; } = 32;

    public void Validate()
    {
        if (!Regex.IsMatch(Template ?? "", "^[a-z][a-z0-9-]{0,60}[a-z0-9]$"))
            throw new ArgumentException("Имя шаблона VM должно содержать строчные латинские буквы, цифры и дефисы.");
        if (Cpus < 1 || MemoryMb < 1024 || DiskGb < 1)
            throw new ArgumentException("Для VM задайте положительное число CPU, не менее 1024 МБ памяти и положительный размер диска.");
    }
}
