// namespace Nico.Core;
//
// public class StructPool<T>
// {
//     private static readonly StructPool<T> s_default = new();
//     public static StructPool<T> Default => s_default;
//
//     private T[] _buffer;
//     private bool[] _counter;
//
//     public StructPool()
//     {
//         _buffer = new T[4];
//         _counter = new bool[4];
//     }
//
//
//     public StructPool(int size)
//     {
//         _buffer = new T[size];
//         _counter = new bool[size];
//     }
//
//     public void Borrow(out T item)
//     {
//         item = default;
//     }
//
//     public void Return(ref T item)
//     {
//
//     }
// }
