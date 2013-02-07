/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2012, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Threading;

namespace NVorbis
{
    static class ACache
    {
        class Scope : IDisposable
        {
            static Scope _global = new Scope(false);

            [ThreadStatic]
            static Scope _current;

            internal static Scope Current
            {
                get { return _current ?? _global; }
            }

            internal static bool IsInScope
            {
                get { return _current != null; }
            }

            #region Sub-Classes

            abstract class BufferStorage
            {
                abstract internal void CloseScope();

                abstract internal Type BufferType { get; }
            }

            class BufferStorage<T> : BufferStorage
            {
                class Item
                {
                    internal T[] Array;
                    internal int Length;
                    internal Item Next;
                }

                class ItemList
                {
                    Item _head;

                    internal void Add(Item item)
                    {
                        do
                        {
                            item.Next = _head;
                        } while (Interlocked.CompareExchange(ref _head, item, item.Next) != item.Next);
                    }

                    internal Item Get(int length)
                    {
                        Item node, nextNode;

                        // if the node is the head, try to rotate it off
                        while ((node = _head) != null && node.Length == length)
                        {
                            if (Interlocked.CompareExchange(ref _head, node.Next, node) == node)
                            {
                                node.Next = null;
                                return node;
                            }
                        }

                        // the node wasn't the head, so look through the list until we find it
                        while (node != null && (nextNode = node.Next) != null)
                        {
                            if (nextNode.Length == length)
                            {
                                if ((node = Interlocked.CompareExchange(ref node.Next, nextNode.Next, nextNode)) == nextNode)
                                {
                                    nextNode.Next = null;
                                    return nextNode;
                                }
                                else
                                {
                                    // try again
                                    continue;
                                }
                            }

                            node = node.Next;
                        }

                        return null;
                    }

                    internal Item Get(T[] array)
                    {
                        Item node, nextNode;

                        // if the node is the head, try to rotate it off
                        while ((node = _head) != null && node.Array == array)
                        {
                            if (Interlocked.CompareExchange(ref _head, node.Next, node) == node)
                            {
                                node.Next = null;
                                return node;
                            }
                        }

                        // the node wasn't the head, so look through the list until we find it
                        while (node != null && (nextNode = node.Next) != null)
                        {
                            if (nextNode.Array == array)
                            {
                                if ((node = Interlocked.CompareExchange(ref node.Next, nextNode.Next, nextNode)) == nextNode)
                                {
                                    nextNode.Next = null;
                                    return nextNode;
                                }
                                else
                                {
                                    // try again
                                    continue;
                                }
                            }

                            node = node.Next;
                        }

                        return null;
                    }

                    internal void ForEach(Action<Item> callback)
                    {
                        var node = _head;
                        while (node != null)
                        {
                            var next = node.Next;
                            callback(node);
                            node = next;
                        }
                    }

                    internal void Clear()
                    {
                        _head = null;
                    }
                }

                ItemList _allocList = new ItemList();
                ItemList _freeList = new ItemList();

                internal T[] Get(int elements, bool clearFirst)
                {
                    var node = _freeList.Get(elements);
                    if (node == null)
                    {
                        // create a new one
                        node = new Item
                        {
                            Array = new T[elements],
                            Length = elements
                        };
                    }
                    else if (clearFirst)
                    {
                        Array.Clear(node.Array, 0, node.Length);
                    }
                    _allocList.Add(node);
                    return node.Array;
                }

                internal void Return(ref T[] buffer)
                {
                    var node = _allocList.Get(buffer);
                    if (node == null)
                    {
                        node = new Item
                        {
                            Array = buffer,
                            Length = buffer.Length
                        };
                    }
                    _freeList.Add(node);
                    buffer = null;
                }

                override internal void CloseScope()
                {
                    var temp = new Action<Item>(CloseScopeForItem);
                    _allocList.ForEach(temp);
                    _freeList.ForEach(temp);

                    _allocList.Clear();
                    _freeList.Clear();
                }

                void CloseScopeForItem(Item node)
                {
                    _global.GetStorage<T>(true)._freeList.Add(node);
                }

                internal override Type BufferType
                {
                    get { return typeof(T); }
                }
            }

            #endregion

            #region Instance Members

            List<BufferStorage> _storage;

            internal Scope(bool setScope)
            {
                _storage = new List<BufferStorage>();
                if (setScope)
                {
                    _current = this;
                }
            }

            BufferStorage<T> GetStorage<T>(bool firstTry)
            {
                while (true)
                {
                    for (int i = 0; i < _storage.Count; i++)
                    {
                        var s = _storage[i];
                        if (s.BufferType == typeof(T))
                        {
                            return (BufferStorage<T>)s;
                        }
                    }
                    _storage.Add(new BufferStorage<T>());
                }
            }

            internal T[] Get<T>(int elements, bool clearFirst)
            {
                return GetStorage<T>(true).Get(elements, clearFirst);
            }

            internal void Return<T>(ref T[] buffer)
            {
                GetStorage<T>(true).Return(ref buffer);
            }

            public void Dispose()
            {
                foreach (var buffer in _storage)
                {
                    buffer.CloseScope();
                }
                _storage.Clear();
                _current = null;
            }

            #endregion
        }

        internal static T[] Get<T>(int elements)
        {
            return Get<T>(elements, true);
        }

        internal static T[] Get<T>(int elements, bool clearFirst)
        {
            return Scope.Current.Get<T>(elements, clearFirst);
        }

        internal static T[][] Get<T>(int firstRankSize, int secondRankSize)
        {
            var temp = Scope.Current.Get<T[]>(firstRankSize, false);
            for (int i = 0; i < firstRankSize; i++)
            {
                temp[i] = Scope.Current.Get<T>(secondRankSize, true);
            }
            return temp;
        }

        internal static T[][][] Get<T>(int firstRankSize, int secondRankSize, int thirdRankSize)
        {
            var temp = Scope.Current.Get<T[][]>(firstRankSize, false);
            for (int i = 0; i < firstRankSize; i++)
            {
                temp[i] = Get<T>(secondRankSize, thirdRankSize);
            }
            return temp;
        }

        internal static void Return<T>(ref T[] buffer)
        {
            Scope.Current.Return<T>(ref buffer);
        }

        internal static void Return<T>(ref T[][] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != null) Return<T>(ref buffer[i]);
            }
            Scope.Current.Return<T[]>(ref buffer);
        }

        internal static void Return<T>(ref T[][][] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != null) Return<T>(ref buffer[i]);
            }
            Scope.Current.Return<T[][]>(ref buffer);
        }

        internal static IDisposable BeginScope()
        {
            if (!Scope.IsInScope)
            {
                return new Scope(true);
            }
            return null;
        }

        internal static void EndScope()
        {
            if (Scope.IsInScope)
            {
                Scope.Current.Dispose();
            }
        }
    }
}
