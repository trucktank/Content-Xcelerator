﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CrownPeak.AccessApiHelper;
using CrownPeak.AccessAPI;

namespace Crownpeak.ContentXcelerator.Migrator
{
	class CmsAssetCache
	{
		private CmsApi _api;
		private int _cacheSize;
		private List<int> _queue;
		private Dictionary<int, WorklistAsset> _assetsById;
		private Dictionary<int, string> _pathsById;
		private Dictionary<string, int> _assetsByPath;
		private Dictionary<int, FolderOptions> _folderProperties;
		// Workflow items
		private List<int> _workflowQueue;
		private Dictionary<int, WorkflowData> _workflowsById;
		private Dictionary<string, int> _workflowsByName;
		private Dictionary<int, WorkflowFilter> _workflowFilters;
		// Publishing items
		private Dictionary<int, List<PublishingProperties>> _publishingProperties;
		private Dictionary<int, string> _publishingPackages;
		private Dictionary<int, string> _states;

		public CmsAssetCache(int size, CmsApi api)
		{
			Debug.WriteLine("Cache create with {0} items", size);
			Reset(size, api);
		}

		public void Reset(int size, CmsApi api)
		{
			Debug.WriteLine("Cache reset with {0} items", size);
			_api = api;
			_cacheSize = size;
			_queue = new List<int>(size);
			_assetsById = new Dictionary<int, WorklistAsset>();
			_assetsByPath = new Dictionary<string, int>();
			_pathsById = new Dictionary<int, string>();
			_folderProperties = new Dictionary<int, FolderOptions>();

			_workflowQueue = new List<int>(size);
			_workflowsById = new Dictionary<int, WorkflowData>();
			_workflowsByName = new Dictionary<string, int>();
			_workflowFilters = new Dictionary<int, WorkflowFilter>();

			_publishingProperties = new Dictionary<int, List<PublishingProperties>>();
			_publishingPackages = new Dictionary<int, string>();
			_states = new Dictionary<int, string>();
		}

		public WorklistAsset GetAsset(int id, bool ensurePath = false)
		{
			if (_queue.Contains(id))
			{
				// Reset the purge queue position for this asset
				_queue.Remove(id);
				_queue.Add(id);
			}
			else
			{
				_queue.Add(id);
				while (_queue.Count > _cacheSize)
				{
					// Purge the oldest item from the list
					int idToRemove = _queue[0];
					_queue.RemoveAt(0);
					if (_assetsById.ContainsKey(idToRemove))
						_assetsById.Remove(idToRemove);
				}
			}
			if (_assetsById.ContainsKey(id))
			{
				Debug.WriteLine("Cache hit for id: {0}", id);
				return _assetsById[id];
			}

			WorklistAsset asset;
			if (_api.Asset.Read(id, out asset))
			{
				Debug.WriteLine("Cache miss for id: {0}", id);

				if (ensurePath && string.IsNullOrWhiteSpace(asset.FullPath))
				{
					// Go find the full path for this asset
					asset.FullPath = GetFullPath(asset);
				}

				UpdateAssetCache(id, asset);
				return asset;
			}
			else
			{
				if (!_assetsById.ContainsKey(id))
				{
					_assetsById.Add(id, null);
				}
				//throw new Exception("Asset not found with id " + id);
				return null;
			}
		}

		public WorklistAsset GetAsset(string idOrPath)
		{
			int id;
			if (int.TryParse(idOrPath, out id))
				return GetAsset(id);

			if (_assetsByPath.ContainsKey(idOrPath))
			{
				Debug.WriteLine("Cache hit for path: " + idOrPath);
				return GetAsset(_assetsByPath[idOrPath]);
			}

			if (_api.Asset.Exists(idOrPath, out id))
			{
				Debug.WriteLine("Cache miss for path: " + idOrPath);
				var asset = GetAsset(id);
				// Make sure that the path is populated
				if (string.IsNullOrWhiteSpace(asset.FullPath))
				{
					asset.FullPath = idOrPath;
					UpdateAssetCache(id, asset);
				}
				return asset;
			}

			// Removed so that the calling code can trap without needing an exception
			//else
			//{
			//	throw new Exception("Asset not found with path " + idOrPath);
			//}
			return null;
		}

		public WorklistAsset UpdateAsset(WorklistAsset asset, Dictionary<string, string> fields)
		{
			WorklistAsset assetOut;
			// TODO: this will add fields, but not remove existing ones
			if (_api.Asset.Update(asset.id, fields, out assetOut))
			{
				UpdateAssetCache(assetOut.id, assetOut);
				return assetOut;
			}

			return null;
		}

		public WorklistAsset CreateAsset(string label, int folderId, int modelId, int type, int? subtype, int templateLanguage, int templateId, int workflowId, Dictionary<string, string> fields)
		{
			WorklistAsset asset;
			if (modelId > 0 && type == 4)
			{
				if (_api.Asset.CreateFolderWithModel(label, folderId, modelId, out asset))
				{
					return asset;
				}
			}
			else
			{
				if (_api.Asset.Create(label, folderId, modelId, type, templateLanguage, templateId, workflowId, out asset, subtype))
				{
					if (fields == null || fields.Count == 0) return asset;

					if (_api.Asset.Update(asset.id, fields, out asset))
					{
						return asset;
					}
				}
			}

			return null;
		}

		public WorklistAsset CreateDigitalAsset(string label, int folderId, int modelId, int workflowId, string base64data)
		{
			WorklistAsset asset;
			if (_api.Asset.Upload(label, folderId, modelId, workflowId, Convert.FromBase64String(base64data), out asset))
			{
				return asset;
			}

			return null;
		}

		public WorklistAsset CreateSite(string label, int folderId)
		{
			WorklistAsset asset;
			if (_api.Asset.CreateSiteRoot(label, folderId, out asset))
			{
				return asset;
			}

			return null;
		}

		public WorklistAsset CreateProject(string label, int folderId)
		{
			WorklistAsset asset;
			if (_api.Asset.CreateProject(label, folderId, out asset))
			{
				return asset;
			}

			return null;
		}

		public WorkflowAsset CreateWorkflow(WorkflowAsset workflow, int folderId)
		{
			var originalName = "";
			var temporaryName = "";
			if (!GetAsset(folderId).FullPath.Equals("/System/Workflows", StringComparison.OrdinalIgnoreCase))
			{
				// We can't control where workflows get created - they go in /System/Workflows
				// Make a temporary name so we can find and move it later
				temporaryName = "Temporary workflow " + new Random().NextDouble().ToString().Split(".".ToCharArray()).Last();
				originalName = workflow.Name;
				workflow.Name = temporaryName;
			}
			WorkflowValidationError[] validationErrors;
			var success = _api.Workflow.Create(workflow, out validationErrors);

			if (success)
			{
				WorkflowAsset workflowAsset;
				_api.Workflow.ReadFull(GetAsset("/System/Workflows/" + workflow.Name).id, out workflowAsset);
				if (!string.IsNullOrEmpty(temporaryName))
				{
					WorklistAsset asset;
					// We need to move our asset into the correct folder
					_api.Asset.Move(workflowAsset.AssetId, folderId, out asset);
					// And rename it so that it matches the original
					_api.Asset.Rename(asset.id, originalName, out asset);
					workflowAsset.Name = originalName;
				}
				return workflowAsset;
			}

			return null;
		}

		public void AddList(IEnumerable<WorklistAsset> assets)
		{
			foreach (var asset in assets)
			{
				var id = asset.id;
				var path = asset.FullPath;
				if (string.IsNullOrWhiteSpace(path))
				{
					path = GetFullPath(asset);
					asset.FullPath = path;
				}

				if (_queue.Contains(id))
				{
					_queue.Remove(id);
					_queue.Add(id);
				}
				if (!_assetsById.ContainsKey(id))
				{
					_assetsById.Add(id, asset);
				}
				if (!string.IsNullOrWhiteSpace(path))
				{
					if (!_assetsByPath.ContainsKey(path))
					{
						_assetsByPath.Add(path, id);
					}
					if (!_pathsById.ContainsKey(id))
					{
						_pathsById.Add(id, path);
					}
				}
			}
		}

		public FolderOptions GetFolderOptions(int id)
		{
			if (_folderProperties.ContainsKey(id)) return _folderProperties[id];

			string header;
			FolderOptionsType type;
			if (_api.AssetProperties.GetFolderOptions(id, out header, out type))
			{
				var fo = new FolderOptions(header, type);
				_folderProperties.Add(id, fo);
				return fo;
			}

			return null;
		}

		public string GetFullPath(WorklistAsset asset)
		{
			// TODO: remove all of this once CMS-6013 is fixed
			if (!string.IsNullOrWhiteSpace(asset.FullPath)) return asset.FullPath;

			if (asset.folder_id.HasValue)
			{
				var folderId = asset.folder_id.Value;
				if (_assetsById.ContainsKey(folderId) && !string.IsNullOrWhiteSpace(_assetsById[folderId].FullPath))
				{
					return _assetsById[folderId].FullPath + "/" + asset.label;
				}

				IEnumerable<WorklistAsset> assets;
				int normalCount, deletedCount, hiddenCount;
				if (_api.Asset.GetList(asset.folder_id.Value, 0, 1, "Label", OrderType.Ascending, VisibilityType.Normal, false,
							false, out assets, out normalCount, out hiddenCount, out deletedCount))
				{
					assets = assets.ToList();
					AddList(assets);
					if (assets.Any())
					{
						var a = assets.First();
						if (!string.IsNullOrWhiteSpace(a.FullPath))
						{
							var path = a.FullPath;
							var i = path.LastIndexOf("/");
							if (i >= 0)
							{
								path = path.Substring(0, i);
							}

							if (_assetsById.ContainsKey(folderId))
							{
								// Make sure that the folder asset has the path for next time
								var folderAsset = _assetsById[folderId];
								folderAsset.FullPath = path;
								_assetsById[folderId] = folderAsset;
							}

							path += "/" + asset.label;
							asset.FullPath = path;

							return path;
						}
					}
				}
			}

			return "";
		}

		public WorkflowData GetWorkflow(int id)
		{
			if (_workflowQueue.Contains(id))
			{
				// Reset the purge queue position for this asset
				_workflowQueue.Remove(id);
				_workflowQueue.Add(id);
			}
			else
			{
				_workflowQueue.Add(id);
				while (_workflowQueue.Count > _cacheSize)
				{
					// Purge the oldest item from the list
					int idToRemove = _workflowQueue[0];
					_workflowQueue.RemoveAt(0);
					if (_workflowsById.ContainsKey(idToRemove))
						_workflowsById.Remove(idToRemove);
				}
			}
			if (_workflowsById.ContainsKey(id))
			{
				Debug.WriteLine("Cache hit for workflow id: {0}", id);
				return _workflowsById[id];
			}

			WorkflowData workflow;
			if (_api.Workflow.Read(id, out workflow))
			{
				Debug.WriteLine("Cache miss for workflow id: {0}", id);

				// Add the workflow to our caches
				if (!_workflowsById.ContainsKey(id))
				{
					_workflowsById.Add(id, workflow);
				}
				else
				{
					_workflowsById[id] = workflow;
				}
				if (!_workflowsByName.ContainsKey(workflow.Name))
				{
					_workflowsByName.Add(workflow.Name, id);
				}
				return workflow;
			}
			else
			{
				if (!_workflowsById.ContainsKey(id))
				{
					_workflowsById.Add(id, null);
				}
				//throw new Exception("Workflow not found with id " + id);
				return null;
			}
		}

		public WorkflowFilter GetWorkflowFilter(int id)
		{
			if (!_workflowFilters.Any())
			{
				WorkflowFilter[] filters;
				if (_api.Workflow.GetWorkflowFilters(out filters))
				{
					_workflowFilters = filters.ToDictionary(p => p.Id, p => p);
				}
			}

			if (_workflowFilters.ContainsKey(id))
				return _workflowFilters[id];

			return null;
		}

		public string GetPublishingPackageName(int id)
		{
			if (!_publishingPackages.Any())
			{
				PublishingPackage[] packages;
				if (_api.Settings.GetPackages(out packages))
				{
					_publishingPackages = packages.ToDictionary(p => p.Id, p => p.Name);
				}
			}

			if (_publishingPackages.ContainsKey(id))
				return _publishingPackages[id];

			return "";
		}

		public List<PublishingProperties> GetPublishingProperties(int id)
		{
			if (_publishingProperties.ContainsKey(id)) return _publishingProperties[id];

			var properties = new List<PublishingProperties>();
			if (_api.AssetProperties.GetPublishingProperties(id, out var deploymentRecords, out var packages))
			{
				foreach (var dr in deploymentRecords)
				{
					properties.Add(new PublishingProperties(
						packages.First(p => p.PackageId == dr.PackageId).PackageName,
						dr.Type == DeploymentType.HtmlDeployment ? PublishingPropertiesType.Templated : PublishingPropertiesType.Digital,
						dr.Filepath,
						dr.Filename,
						dr.Extension,
						dr.Layout
					));
				}

				return properties;
			}

			return null;
		}

		public string GetStatusName(int id)
		{
			if (!_states.Any())
			{
				if (_api.Asset.Exists("/System/States", out var statesId))
				{
					IEnumerable<WorklistAsset> assets;
					int normal, hidden, deleted;
					if (_api.Asset.GetList(statesId, 0, 100, "", OrderType.Ascending, VisibilityType.Normal, true, true, out assets, out normal, out hidden, out deleted))
					{
						_states = assets.ToDictionary(a => a.id, a => a.label);
					}
				}
			}

			if (_states.ContainsKey(id))
				return _states[id];

			return "";
		}

		private void UpdateAssetCache(int id, WorklistAsset asset)
		{
			// Add the asset to our caches
			if (!_assetsById.ContainsKey(id))
			{
				_assetsById.Add(id, asset);
			}
			else
			{
				_assetsById[id] = asset;
			}
			if (!string.IsNullOrWhiteSpace(asset.FullPath))
			{
				if (!_assetsByPath.ContainsKey(asset.FullPath))
				{
					_assetsByPath.Add(asset.FullPath, id);
				}
				if (!_pathsById.ContainsKey(id))
				{
					_pathsById.Add(id, asset.FullPath);
				}
			}
		}
	}
}
