using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Api.V1.Indexers
{
    [V1ApiController]
    public class IndexerFlagController : Controller
    {
        [HttpGet]
        public List<IndexerFlagResource> GetAll()
        {
            return Enum.GetValues(typeof(IndexerFlags)).Cast<IndexerFlags>().Select(f => new IndexerFlagResource
            {
                Id = (int)f,
                Name = GetDisplayName(f)
            }).ToList();
        }

        private static string GetDisplayName(IndexerFlags flag)
        {
            return flag switch
            {
                IndexerFlags.Freeleech => "Freeleech",
                IndexerFlags.Halfleech => "50% Freeleech",
                IndexerFlags.DoubleUpload => "Double Upload",
                IndexerFlags.Internal => "Internal",
                IndexerFlags.Scene => "Scene",
                IndexerFlags.Freeleech75 => "75% Freeleech",
                IndexerFlags.Freeleech25 => "25% Freeleech",
                IndexerFlags.VipExclusive => "VIP Only",
                IndexerFlags.VipFreeleech => "VIP Freeleech",
                _ => flag.ToString()
            };
        }
    }
}
