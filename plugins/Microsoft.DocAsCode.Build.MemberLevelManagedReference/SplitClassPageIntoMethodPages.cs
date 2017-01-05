// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.ManagedReference
{
    using System;
    using System.Collections.Generic;
    using System.Composition;
    using System.Linq;
    using System.IO;
    using System.Collections.Immutable;

    using Microsoft.DocAsCode.Build.Common;
    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.DataContracts.Common;
    using Microsoft.DocAsCode.DataContracts.ManagedReference;
    using Microsoft.DocAsCode.Plugins;

    [Export("ManagedReferenceDocumentProcessor", typeof(IDocumentBuildStep))]
    public class SplitClassPageIntoMethodPages : BaseDocumentBuildStep
    {
        private const string MemberTypeKey = "memberType";
        private const string TopicUidKey = "topicUid";
        private const char OverloadLastChar = '*';
        public override string Name => nameof(SplitClassPageIntoMethodPages);

        public override int BuildOrder => 1;

        /// <summary>
        /// Extract: group with overload
        /// </summary>
        /// <param name="models"></param>
        /// <param name="host"></param>
        /// <returns></returns>
        public override IEnumerable<FileModel> Prebuild(ImmutableList<FileModel> models, IHostService host)
        {
            var collection = new List<FileModel>(models);

            // Separate items into different models if the PageViewModel contains more than one item
            var treeMapping = new Dictionary<string, List<TreeItem>>();
            foreach (var model in models)
            {
                if (model.Type != DocumentType.Article) { break; }
                var page = (PageViewModel)model.Content;
                SplittedResult result;
                if (TrySplitModelToOverloadLevel(model, out result))
                {
                    treeMapping.Add(result.Uid, result.TreeItems);
                    collection.AddRange(result.Models);
                }
            }

            if (treeMapping.Count > 0)
            {
                host.RegisterRestructuringTableOfContent(item => RestructureTableOfContent(item, treeMapping));
            }

            return collection;
        }

        private bool TrySplitModelToOverloadLevel(FileModel model, out SplittedResult result)
        {
            result = null;
            if (model.Type != DocumentType.Article)
            {
                return false;
            }

            var page = (PageViewModel)model.Content;

            if (page.Items.Count <= 1)
            {
                return false;
            }

            var primaryItem = page.Items[0];
            var itemsToSplit = page.Items.Skip(1);

            var children = new List<TreeItem>();
            var splittedModels = new List<FileModel>();

            var group = (from item in itemsToSplit group item by item.Overload into o select o).ToList();

            // Per Overload per page
            foreach (var overload in group)
            {
                if (overload.Key == null)
                {
                    foreach (var i in overload)
                    {
                        var m = GenerateNonOverloadPage(page, model, i);
                        splittedModels.Add(m.FileModel);
                        children.Add(m.TreeItem);
                    }
                }
                else
                {
                    var m = GenerateOverloadPage(page, model, overload);
                    splittedModels.Add(m.FileModel);
                    children.Add(m.TreeItem);
                }
            }

            // Convert children to references
            page.References = itemsToSplit.Select(s => ConvertToReference(s)).Concat(page.References).ToList();

            page.Items = new List<ItemViewModel> { primaryItem };
            model.Uids = CalculateUids(page, model.LocalPathFromRoot);
            model.Content = page;

            result = new SplittedResult(primaryItem.Uid, children, splittedModels);
            return true;
        }

        private void RestructureTableOfContent(TreeItem tree, Dictionary<string, List<TreeItem>> treeMapping)
        {
            var navigator = new TreeNavigator(tree);
            while (treeMapping.Count > 0)
            {
                string currentUid = string.Empty;
                var matched = navigator.MoveTo(s =>
                {
                    var tocItem = s as TreeItem;
                    if (tocItem == null)
                    {
                        return false;
                    }

                    // Only TopicUID is envolved in splitting
                    // If TOC explicitly references to the .yml file, TOC will not change
                    string itemUid = GetTopicUid(tocItem.Metadata);
                    if (itemUid == null)
                    {
                        return false;
                    }
                    if (treeMapping.ContainsKey(itemUid))
                    {
                        currentUid = itemUid;
                        return true;
                    }
                    return false;
                });

                if (matched)
                {
                    foreach (var i in treeMapping[currentUid])
                    {
                        navigator.AppendChild(i);
                    }
                    treeMapping.Remove(currentUid);
                }
            }
        }

        private ModelWrapper GenerateNonOverloadPage(PageViewModel page, FileModel model, ItemViewModel item)
        {
            var newPage = ExtractPageViewModel(page, new List<ItemViewModel> { item });
            var newModel = GenerateNewFileModel(model, newPage, item.Uid);
            var tree = Convert(item);
            return new ModelWrapper(newPage, newModel, tree);
        }

        private ModelWrapper GenerateOverloadPage(PageViewModel page, FileModel model, IGrouping<string, ItemViewModel> overload)
        {
            var primaryItem = page.Items[0];

            // For ctor, rename #ctor to class name
            var firstMember = overload.First();
            var key = overload.Key;

            var newPrimaryItem = new ItemViewModel
            {
                Uid = key,
                Children = overload.Select(s => s.Uid).ToList(),
                Type = MemberType.OverloadGroup,
                Name = GetName(key, primaryItem.Uid, firstMember.Type == MemberType.Constructor),
            };

            newPrimaryItem.Metadata[MemberTypeKey] = firstMember.Type;
            var newPage = ExtractPageViewModel(page, new List<ItemViewModel> { newPrimaryItem }.Concat(overload).ToList());
            var newModel = GenerateNewFileModel(model, newPage, overload.Key.Trim(OverloadLastChar));
            var tree = Convert(
                newPrimaryItem,
                new Dictionary<string, object>
                {
                    ["type"] = firstMember.Type
                });
            return new ModelWrapper(newPage, newModel, tree);
        }

        private string GetTopicUid(Dictionary<string, object> metadata)
        {
            object uid;
            if (metadata != null && metadata.TryGetValue(TopicUidKey, out uid))
            {
                return uid as string;
            }

            return null;
        }

        private string GetName(string overload, string parent, bool isCtor)
        {
            if (string.IsNullOrEmpty(overload) || string.IsNullOrEmpty(parent))
            {
                return overload;
            }

            if (isCtor)
            {
                // Replace #ctor with parent name
                var parts = parent.Split('.');
                return parts[parts.Length - 1];
            }

            if (overload.StartsWith(parent))
            {
                return overload.Substring(parent.Length).Trim('.', OverloadLastChar);
            }
            return overload;
        }

        private ReferenceViewModel ConvertToReference(ItemViewModel item)
        {
            return new ReferenceViewModel
            {
                Uid = item.Uid,
                CommentId = item.CommentId,
                Name = item.Name,
                FullName = item.FullName,
            };
        }

        private TreeItem Convert(ItemViewModel item, Dictionary<string, object> metadata = null)
        {
            var result = new TreeItem();
            result.Metadata = new Dictionary<string, object>()
            {
                ["name"] = item.Name,
                ["name.csharp"] = item.NameForCSharp,
                ["name.vb"] = item.NameForVB,
                ["fullName"] = item.FullName,
                ["fullName.csharp"] = item.FullNameForCSharp,
                ["fullName.vb"] = item.FullNameForVB,
                ["topicUid"] = item.Uid,
                ["type"] = item.Type.ToString(),
            };

            if (metadata != null)
            {
                foreach(var pair in metadata)
                {
                    result.Metadata[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        private PageViewModel ExtractPageViewModelRange(PageViewModel item, int index, int count)
        {
            var length = item.Items.Count;
            if (index < 0)
            {
                throw new IndexOutOfRangeException($"Negative index number {index} is not allowed");
            }
            if (count < 0)
            {
                throw new IndexOutOfRangeException($"Negative count number {count} is not allowed");
            }
            if (index + count > length)
            {
                throw new IndexOutOfRangeException($"Current page only contains {length} items while the query requests {count} items from {index}");
            }

            var extractedItems = item.Items.GetRange(index, count);
            return ExtractPageViewModel(item, extractedItems);
        }

        private PageViewModel ExtractPageViewModel(PageViewModel page, List<ItemViewModel> items)
        {
            var newPage = new PageViewModel
            {
                Items = items,
                Metadata = page.Metadata,
                References = page.References,
                ShouldSkipMarkup = page.ShouldSkipMarkup
            };
            return newPage;
        }

        private FileModel GenerateNewFileModel(FileModel model, PageViewModel newPage, string key)
        {
            var initialFile = model.FileAndType.File;
            var extension = Path.GetExtension(initialFile);
            var directory = Path.GetDirectoryName(initialFile);
            var newFileName = PathUtility.ToValidFilePath(key, '-');
            var newFileAndType = new FileAndType(model.FileAndType.BaseDir, string.Join("/", directory, newFileName + extension), model.FileAndType.Type, model.FileAndType.SourceDir, model.FileAndType.DestinationDir);
            var newModel = new FileModel(newFileAndType, newPage, null, model.Serializer);
            newModel.LocalPathFromRoot = model.LocalPathFromRoot;
            newModel.Uids = CalculateUids(newPage, model.LocalPathFromRoot);
            return newModel;
        }

        private ImmutableArray<UidDefinition> CalculateUids(PageViewModel page, string file)
        {
            return (from item in page.Items select new UidDefinition(item.Uid, file)).ToImmutableArray();
        }

        private sealed class SplittedResult
        {
            public string Uid { get; }
            public List<TreeItem> TreeItems { get; }
            public List<FileModel> Models { get; }

            public SplittedResult(string uid, List<TreeItem> items, List<FileModel> models)
            {
                Uid = uid;
                TreeItems = items;
                Models = models;
            }
        }

        private sealed class ModelWrapper
        {
            public PageViewModel PageViewModel { get; }
            public FileModel FileModel { get; }
            public TreeItem TreeItem { get; }

            public ModelWrapper(PageViewModel page, FileModel fileModel, TreeItem tree)
            {
                PageViewModel = page;
                FileModel = fileModel;
                TreeItem = tree;
            }
        }
    }
}
