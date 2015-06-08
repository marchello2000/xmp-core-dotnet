// =================================================================================================
// ADOBE SYSTEMS INCORPORATED
// Copyright 2006 Adobe Systems Incorporated
// All Rights Reserved
//
// NOTICE:  Adobe permits you to use, modify, and distribute this file in accordance with the terms
// of the Adobe license agreement accompanying it.
// =================================================================================================

using System.Collections;
using Com.Adobe.Xmp.Impl.Xpath;
using Com.Adobe.Xmp.Options;
using Com.Adobe.Xmp.Properties;
using Sharpen;

namespace Com.Adobe.Xmp.Impl
{
    /// <since>Aug 18, 2006</since>
    public static class XmpNormalizer
    {
        /// <summary>caches the correct dc-property array forms</summary>
        private static IDictionary _dcArrayForms;

        static XmpNormalizer()
        {
            InitDcArrays();
        }

        // EMPTY
        /// <summary>Normalizes a raw parsed XMPMeta-Object</summary>
        /// <param name="xmp">the raw metadata object</param>
        /// <param name="options">the parsing options</param>
        /// <returns>Returns the normalized metadata object</returns>
        /// <exception cref="XmpException">Collects all severe processing errors.</exception>
        internal static IXmpMeta Process(XmpMeta xmp, ParseOptions options)
        {
            XmpNode tree = xmp.GetRoot();
            TouchUpDataModel(xmp);
            MoveExplicitAliases(tree, options);
            TweakOldXmp(tree);
            DeleteEmptySchemas(tree);
            return xmp;
        }

        /// <summary>
        /// Tweak old XMP: Move an instance ID from rdf:about to the
        /// <em>xmpMM:InstanceID</em> property.
        /// </summary>
        /// <remarks>
        /// Tweak old XMP: Move an instance ID from rdf:about to the
        /// <em>xmpMM:InstanceID</em> property. An old instance ID usually looks
        /// like &quot;uuid:bac965c4-9d87-11d9-9a30-000d936b79c4&quot;, plus InDesign
        /// 3.0 wrote them like &quot;bac965c4-9d87-11d9-9a30-000d936b79c4&quot;. If
        /// the name looks like a UUID simply move it to <em>xmpMM:InstanceID</em>,
        /// don't worry about any existing <em>xmpMM:InstanceID</em>. Both will
        /// only be present when a newer file with the <em>xmpMM:InstanceID</em>
        /// property is updated by an old app that uses <em>rdf:about</em>.
        /// </remarks>
        /// <param name="tree">the root of the metadata tree</param>
        /// <exception cref="XmpException">Thrown if tweaking fails.</exception>
        private static void TweakOldXmp(XmpNode tree)
        {
            if (tree.GetName() != null && tree.GetName().Length >= Utils.UuidLength)
            {
                string nameStr = tree.GetName().ToLower();
                if (nameStr.StartsWith("uuid:"))
                {
                    nameStr = Runtime.Substring(nameStr, 5);
                }
                if (Utils.CheckUuidFormat(nameStr))
                {
                    // move UUID to xmpMM:InstanceID and remove it from the root node
                    XmpPath path = XmpPathParser.ExpandXPath(XmpConstConstants.NsXmpMm, "InstanceID");
                    XmpNode idNode = XmpNodeUtils.FindNode(tree, path, true, null);
                    if (idNode != null)
                    {
                        idNode.SetOptions(null);
                        // Clobber any existing xmpMM:InstanceID.
                        idNode.SetValue("uuid:" + nameStr);
                        idNode.RemoveChildren();
                        idNode.RemoveQualifiers();
                        tree.SetName(null);
                    }
                    else
                    {
                        throw new XmpException("Failure creating xmpMM:InstanceID", XmpErrorCode.Internalfailure);
                    }
                }
            }
        }

        /// <summary>Visit all schemas to do general fixes and handle special cases.</summary>
        /// <param name="xmp">the metadata object implementation</param>
        /// <exception cref="XmpException">Thrown if the normalisation fails.</exception>
        private static void TouchUpDataModel(XmpMeta xmp)
        {
            // make sure the DC schema is existing, because it might be needed within the normalization
            // if not touched it will be removed by removeEmptySchemas
            XmpNodeUtils.FindSchemaNode(xmp.GetRoot(), XmpConstConstants.NsDc, true);
            // Do the special case fixes within each schema.
            for (IIterator it = xmp.GetRoot().IterateChildren(); it.HasNext(); )
            {
                XmpNode currSchema = (XmpNode)it.Next();
                if (XmpConstConstants.NsDc.Equals(currSchema.GetName()))
                {
                    NormalizeDcArrays(currSchema);
                }
                else
                {
                    if (XmpConstConstants.NsExif.Equals(currSchema.GetName()))
                    {
                        // Do a special case fix for exif:GPSTimeStamp.
                        FixGpsTimeStamp(currSchema);
                        XmpNode arrayNode = XmpNodeUtils.FindChildNode(currSchema, "exif:UserComment", false);
                        if (arrayNode != null)
                        {
                            RepairAltText(arrayNode);
                        }
                    }
                    else
                    {
                        if (XmpConstConstants.NsDm.Equals(currSchema.GetName()))
                        {
                            // Do a special case migration of xmpDM:copyright to
                            // dc:rights['x-default'].
                            XmpNode dmCopyright = XmpNodeUtils.FindChildNode(currSchema, "xmpDM:copyright", false);
                            if (dmCopyright != null)
                            {
                                MigrateAudioCopyright(xmp, dmCopyright);
                            }
                        }
                        else
                        {
                            if (XmpConstConstants.NsXmpRights.Equals(currSchema.GetName()))
                            {
                                XmpNode arrayNode = XmpNodeUtils.FindChildNode(currSchema, "xmpRights:UsageTerms", false);
                                if (arrayNode != null)
                                {
                                    RepairAltText(arrayNode);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Undo the denormalization performed by the XMP used in Acrobat 5.<br />
        /// If a Dublin Core array had only one item, it was serialized as a simple
        /// property.
        /// </summary>
        /// <remarks>
        /// Undo the denormalization performed by the XMP used in Acrobat 5.<br />
        /// If a Dublin Core array had only one item, it was serialized as a simple
        /// property. <br />
        /// The <code>xml:lang</code> attribute was dropped from an
        /// <code>alt-text</code> item if the language was <code>x-default</code>.
        /// </remarks>
        /// <param name="dcSchema">the DC schema node</param>
        /// <exception cref="XmpException">Thrown if normalization fails</exception>
        private static void NormalizeDcArrays(XmpNode dcSchema)
        {
            for (int i = 1; i <= dcSchema.GetChildrenLength(); i++)
            {
                XmpNode currProp = dcSchema.GetChild(i);
                PropertyOptions arrayForm = (PropertyOptions)_dcArrayForms.Get(currProp.GetName());
                if (arrayForm == null)
                {
                    continue;
                }
                if (currProp.GetOptions().IsSimple())
                {
                    // create a new array and add the current property as child,
                    // if it was formerly simple
                    XmpNode newArray = new XmpNode(currProp.GetName(), arrayForm);
                    currProp.SetName(XmpConstConstants.ArrayItemName);
                    newArray.AddChild(currProp);
                    dcSchema.ReplaceChild(i, newArray);
                    // fix language alternatives
                    if (arrayForm.IsArrayAltText() && !currProp.GetOptions().GetHasLanguage())
                    {
                        XmpNode newLang = new XmpNode(XmpConstConstants.XmlLang, XmpConstConstants.XDefault, null);
                        currProp.AddQualifier(newLang);
                    }
                }
                else
                {
                    // clear array options and add corrected array form if it has been an array before
                    currProp.GetOptions().SetOption(PropertyOptions.Array | PropertyOptions.ArrayOrdered | PropertyOptions.ArrayAlternate | PropertyOptions.ArrayAltText, false);
                    currProp.GetOptions().MergeWith(arrayForm);
                    if (arrayForm.IsArrayAltText())
                    {
                        // applying for "dc:description", "dc:rights", "dc:title"
                        RepairAltText(currProp);
                    }
                }
            }
        }

        /// <summary>Make sure that the array is well-formed AltText.</summary>
        /// <remarks>
        /// Make sure that the array is well-formed AltText. Each item must be simple
        /// and have an "xml:lang" qualifier. If repairs are needed, keep simple
        /// non-empty items by adding the "xml:lang" with value "x-repair".
        /// </remarks>
        /// <param name="arrayNode">the property node of the array to repair.</param>
        /// <exception cref="XmpException">Forwards unexpected exceptions.</exception>
        private static void RepairAltText(XmpNode arrayNode)
        {
            if (arrayNode == null || !arrayNode.GetOptions().IsArray())
            {
                // Already OK or not even an array.
                return;
            }
            // fix options
            arrayNode.GetOptions().SetArrayOrdered(true).SetArrayAlternate(true).SetArrayAltText(true);
            for (IIterator it = arrayNode.IterateChildren(); it.HasNext(); )
            {
                XmpNode currChild = (XmpNode)it.Next();
                if (currChild.GetOptions().IsCompositeProperty())
                {
                    // Delete non-simple children.
                    it.Remove();
                }
                else
                {
                    if (!currChild.GetOptions().GetHasLanguage())
                    {
                        string childValue = currChild.GetValue();
                        if (string.IsNullOrEmpty(childValue))
                        {
                            // Delete empty valued children that have no xml:lang.
                            it.Remove();
                        }
                        else
                        {
                            // Add an xml:lang qualifier with the value "x-repair".
                            XmpNode repairLang = new XmpNode(XmpConstConstants.XmlLang, "x-repair", null);
                            currChild.AddQualifier(repairLang);
                        }
                    }
                }
            }
        }

        /// <summary>Visit all of the top level nodes looking for aliases.</summary>
        /// <remarks>
        /// Visit all of the top level nodes looking for aliases. If there is
        /// no base, transplant the alias subtree. If there is a base and strict
        /// aliasing is on, make sure the alias and base subtrees match.
        /// </remarks>
        /// <param name="tree">the root of the metadata tree</param>
        /// <param name="options">th parsing options</param>
        /// <exception cref="XmpException">Forwards XMP errors</exception>
        private static void MoveExplicitAliases(XmpNode tree, ParseOptions options)
        {
            if (!tree.GetHasAliases())
            {
                return;
            }
            tree.SetHasAliases(false);
            bool strictAliasing = options.GetStrictAliasing();
            for (IIterator schemaIt = tree.GetUnmodifiableChildren().Iterator(); schemaIt.HasNext(); )
            {
                XmpNode currSchema = (XmpNode)schemaIt.Next();
                if (!currSchema.GetHasAliases())
                {
                    continue;
                }
                for (IIterator propertyIt = currSchema.IterateChildren(); propertyIt.HasNext(); )
                {
                    XmpNode currProp = (XmpNode)propertyIt.Next();
                    if (!currProp.IsAlias())
                    {
                        continue;
                    }
                    currProp.SetAlias(false);
                    // Find the base path, look for the base schema and root node.
                    IXmpAliasInfo info = XmpMetaFactory.GetSchemaRegistry().FindAlias(currProp.GetName());
                    if (info != null)
                    {
                        // find or create schema
                        XmpNode baseSchema = XmpNodeUtils.FindSchemaNode(tree, info.GetNamespace(), null, true);
                        baseSchema.SetImplicit(false);
                        XmpNode baseNode = XmpNodeUtils.FindChildNode(baseSchema, info.GetPrefix() + info.GetPropName(), false);
                        if (baseNode == null)
                        {
                            if (info.GetAliasForm().IsSimple())
                            {
                                // A top-to-top alias, transplant the property.
                                // change the alias property name to the base name
                                string qname = info.GetPrefix() + info.GetPropName();
                                currProp.SetName(qname);
                                baseSchema.AddChild(currProp);
                                // remove the alias property
                                propertyIt.Remove();
                            }
                            else
                            {
                                // An alias to an array item,
                                // create the array and transplant the property.
                                baseNode = new XmpNode(info.GetPrefix() + info.GetPropName(), info.GetAliasForm().ToPropertyOptions());
                                baseSchema.AddChild(baseNode);
                                TransplantArrayItemAlias(propertyIt, currProp, baseNode);
                            }
                        }
                        else
                        {
                            if (info.GetAliasForm().IsSimple())
                            {
                                // The base node does exist and this is a top-to-top alias.
                                // Check for conflicts if strict aliasing is on.
                                // Remove and delete the alias subtree.
                                if (strictAliasing)
                                {
                                    CompareAliasedSubtrees(currProp, baseNode, true);
                                }
                                propertyIt.Remove();
                            }
                            else
                            {
                                // This is an alias to an array item and the array exists.
                                // Look for the aliased item.
                                // Then transplant or check & delete as appropriate.
                                XmpNode itemNode = null;
                                if (info.GetAliasForm().IsArrayAltText())
                                {
                                    int xdIndex = XmpNodeUtils.LookupLanguageItem(baseNode, XmpConstConstants.XDefault);
                                    if (xdIndex != -1)
                                    {
                                        itemNode = baseNode.GetChild(xdIndex);
                                    }
                                }
                                else
                                {
                                    if (baseNode.HasChildren())
                                    {
                                        itemNode = baseNode.GetChild(1);
                                    }
                                }
                                if (itemNode == null)
                                {
                                    TransplantArrayItemAlias(propertyIt, currProp, baseNode);
                                }
                                else
                                {
                                    if (strictAliasing)
                                    {
                                        CompareAliasedSubtrees(currProp, itemNode, true);
                                    }
                                    propertyIt.Remove();
                                }
                            }
                        }
                    }
                }
                currSchema.SetHasAliases(false);
            }
        }

        /// <summary>Moves an alias node of array form to another schema into an array</summary>
        /// <param name="propertyIt">the property iterator of the old schema (used to delete the property)</param>
        /// <param name="childNode">the node to be moved</param>
        /// <param name="baseArray">the base array for the array item</param>
        /// <exception cref="XmpException">Forwards XMP errors</exception>
        private static void TransplantArrayItemAlias(IIterator propertyIt, XmpNode childNode, XmpNode baseArray)
        {
            if (baseArray.GetOptions().IsArrayAltText())
            {
                if (childNode.GetOptions().GetHasLanguage())
                {
                    throw new XmpException("Alias to x-default already has a language qualifier", XmpErrorCode.Badxmp);
                }
                XmpNode langQual = new XmpNode(XmpConstConstants.XmlLang, XmpConstConstants.XDefault, null);
                childNode.AddQualifier(langQual);
            }
            propertyIt.Remove();
            childNode.SetName(XmpConstConstants.ArrayItemName);
            baseArray.AddChild(childNode);
        }

        /// <summary>Fixes the GPS Timestamp in EXIF.</summary>
        /// <param name="exifSchema">the EXIF schema node</param>
        /// <exception cref="XmpException">Thrown if the date conversion fails.</exception>
        private static void FixGpsTimeStamp(XmpNode exifSchema)
        {
            // Note: if dates are not found the convert-methods throws an exceptions,
            //          and this methods returns.
            XmpNode gpsDateTime = XmpNodeUtils.FindChildNode(exifSchema, "exif:GPSTimeStamp", false);
            if (gpsDateTime == null)
            {
                return;
            }
            try
            {
                IXmpDateTime binGpsStamp;
                IXmpDateTime binOtherDate;
                binGpsStamp = Xmp.XmpUtils.ConvertToDate(gpsDateTime.GetValue());
                if (binGpsStamp.GetYear() != 0 || binGpsStamp.GetMonth() != 0 || binGpsStamp.GetDay() != 0)
                {
                    return;
                }
                XmpNode otherDate = XmpNodeUtils.FindChildNode(exifSchema, "exif:DateTimeOriginal", false);
                if (otherDate == null)
                {
                    otherDate = XmpNodeUtils.FindChildNode(exifSchema, "exif:DateTimeDigitized", false);
                }
                binOtherDate = Xmp.XmpUtils.ConvertToDate(otherDate.GetValue());
                Calendar cal = binGpsStamp.GetCalendar();
                cal.Set(CalendarEnum.Year, binOtherDate.GetYear());
                cal.Set(CalendarEnum.Month, binOtherDate.GetMonth());
                cal.Set(CalendarEnum.DayOfMonth, binOtherDate.GetDay());
                binGpsStamp = new XmpDateTime(cal);
                gpsDateTime.SetValue(Xmp.XmpUtils.ConvertFromDate(binGpsStamp));
            }
            catch (XmpException)
            {
                // Don't let a missing or bad date stop other things.
                return;
            }
        }

        /// <summary>Remove all empty schemas from the metadata tree that were generated during the rdf parsing.</summary>
        /// <param name="tree">the root of the metadata tree</param>
        private static void DeleteEmptySchemas(XmpNode tree)
        {
            // Delete empty schema nodes. Do this last, other cleanup can make empty
            // schema.
            for (IIterator it = tree.IterateChildren(); it.HasNext(); )
            {
                XmpNode schema = (XmpNode)it.Next();
                if (!schema.HasChildren())
                {
                    it.Remove();
                }
            }
        }

        /// <summary>The outermost call is special.</summary>
        /// <remarks>
        /// The outermost call is special. The names almost certainly differ. The
        /// qualifiers (and hence options) will differ for an alias to the x-default
        /// item of a langAlt array.
        /// </remarks>
        /// <param name="aliasNode">the alias node</param>
        /// <param name="baseNode">the base node of the alias</param>
        /// <param name="outerCall">marks the outer call of the recursion</param>
        /// <exception cref="XmpException">Forwards XMP errors</exception>
        private static void CompareAliasedSubtrees(XmpNode aliasNode, XmpNode baseNode, bool outerCall)
        {
            if (!aliasNode.GetValue().Equals(baseNode.GetValue()) || aliasNode.GetChildrenLength() != baseNode.GetChildrenLength())
            {
                throw new XmpException("Mismatch between alias and base nodes", XmpErrorCode.Badxmp);
            }
            if (!outerCall && (!aliasNode.GetName().Equals(baseNode.GetName()) || !aliasNode.GetOptions().Equals(baseNode.GetOptions()) || aliasNode.GetQualifierLength() != baseNode.GetQualifierLength()))
            {
                throw new XmpException("Mismatch between alias and base nodes", XmpErrorCode.Badxmp);
            }
            for (IIterator an = aliasNode.IterateChildren(), bn = baseNode.IterateChildren(); an.HasNext() && bn.HasNext(); )
            {
                XmpNode aliasChild = (XmpNode)an.Next();
                XmpNode baseChild = (XmpNode)bn.Next();
                CompareAliasedSubtrees(aliasChild, baseChild, false);
            }
            for (IIterator an1 = aliasNode.IterateQualifier(), bn1 = baseNode.IterateQualifier(); an1.HasNext() && bn1.HasNext(); )
            {
                XmpNode aliasQual = (XmpNode)an1.Next();
                XmpNode baseQual = (XmpNode)bn1.Next();
                CompareAliasedSubtrees(aliasQual, baseQual, false);
            }
        }

        /// <summary>
        /// The initial support for WAV files mapped a legacy ID3 audio copyright
        /// into a new xmpDM:copyright property.
        /// </summary>
        /// <remarks>
        /// The initial support for WAV files mapped a legacy ID3 audio copyright
        /// into a new xmpDM:copyright property. This is special case code to migrate
        /// that into dc:rights['x-default']. The rules:
        /// <pre>
        /// 1. If there is no dc:rights array, or an empty array -
        /// Create one with dc:rights['x-default'] set from double linefeed and xmpDM:copyright.
        /// 2. If there is a dc:rights array but it has no x-default item -
        /// Create an x-default item as a copy of the first item then apply rule #3.
        /// 3. If there is a dc:rights array with an x-default item,
        /// Look for a double linefeed in the value.
        /// A. If no double linefeed, compare the x-default value to the xmpDM:copyright value.
        /// A1. If they match then leave the x-default value alone.
        /// A2. Otherwise, append a double linefeed and
        /// the xmpDM:copyright value to the x-default value.
        /// B. If there is a double linefeed, compare the trailing text to the xmpDM:copyright value.
        /// B1. If they match then leave the x-default value alone.
        /// B2. Otherwise, replace the trailing x-default text with the xmpDM:copyright value.
        /// 4. In all cases, delete the xmpDM:copyright property.
        /// </pre>
        /// </remarks>
        /// <param name="xmp">the metadata object</param>
        /// <param name="dmCopyright">the "dm:copyright"-property</param>
        private static void MigrateAudioCopyright(IXmpMeta xmp, XmpNode dmCopyright)
        {
            try
            {
                XmpNode dcSchema = XmpNodeUtils.FindSchemaNode(((XmpMeta)xmp).GetRoot(), XmpConstConstants.NsDc, true);
                string dmValue = dmCopyright.GetValue();
                string doubleLf = "\n\n";
                XmpNode dcRightsArray = XmpNodeUtils.FindChildNode(dcSchema, "dc:rights", false);
                if (dcRightsArray == null || !dcRightsArray.HasChildren())
                {
                    // 1. No dc:rights array, create from double linefeed and xmpDM:copyright.
                    dmValue = doubleLf + dmValue;
                    xmp.SetLocalizedText(XmpConstConstants.NsDc, "rights", string.Empty, XmpConstConstants.XDefault, dmValue, null);
                }
                else
                {
                    int xdIndex = XmpNodeUtils.LookupLanguageItem(dcRightsArray, XmpConstConstants.XDefault);
                    if (xdIndex < 0)
                    {
                        // 2. No x-default item, create from the first item.
                        string firstValue = dcRightsArray.GetChild(1).GetValue();
                        xmp.SetLocalizedText(XmpConstConstants.NsDc, "rights", string.Empty, XmpConstConstants.XDefault, firstValue, null);
                        xdIndex = XmpNodeUtils.LookupLanguageItem(dcRightsArray, XmpConstConstants.XDefault);
                    }
                    // 3. Look for a double linefeed in the x-default value.
                    XmpNode defaultNode = dcRightsArray.GetChild(xdIndex);
                    string defaultValue = defaultNode.GetValue();
                    int lfPos = defaultValue.IndexOf(doubleLf);
                    if (lfPos < 0)
                    {
                        // 3A. No double LF, compare whole values.
                        if (!dmValue.Equals(defaultValue))
                        {
                            // 3A2. Append the xmpDM:copyright to the x-default
                            // item.
                            defaultNode.SetValue(defaultValue + doubleLf + dmValue);
                        }
                    }
                    else
                    {
                        // 3B. Has double LF, compare the tail.
                        if (!Runtime.Substring(defaultValue, lfPos + 2).Equals(dmValue))
                        {
                            // 3B2. Replace the x-default tail.
                            defaultNode.SetValue(Runtime.Substring(defaultValue, 0, lfPos + 2) + dmValue);
                        }
                    }
                }
                // 4. Get rid of the xmpDM:copyright.
                dmCopyright.GetParent().RemoveChild(dmCopyright);
            }
            catch (XmpException)
            {
            }
        }

        // Don't let failures (like a bad dc:rights form) stop other
        // cleanup.
        /// <summary>
        /// Initializes the map that contains the known arrays, that are fixed by <see cref="NormalizeDcArrays"/>.
        /// </summary>
        private static void InitDcArrays()
        {
            _dcArrayForms = new Hashtable();
            // Properties supposed to be a "Bag".
            PropertyOptions bagForm = new PropertyOptions();
            bagForm.SetArray(true);
            _dcArrayForms.Put("dc:contributor", bagForm);
            _dcArrayForms.Put("dc:language", bagForm);
            _dcArrayForms.Put("dc:publisher", bagForm);
            _dcArrayForms.Put("dc:relation", bagForm);
            _dcArrayForms.Put("dc:subject", bagForm);
            _dcArrayForms.Put("dc:type", bagForm);
            // Properties supposed to be a "Seq".
            PropertyOptions seqForm = new PropertyOptions();
            seqForm.SetArray(true);
            seqForm.SetArrayOrdered(true);
            _dcArrayForms.Put("dc:creator", seqForm);
            _dcArrayForms.Put("dc:date", seqForm);
            // Properties supposed to be an "Alt" in alternative-text form.
            PropertyOptions altTextForm = new PropertyOptions();
            altTextForm.SetArray(true);
            altTextForm.SetArrayOrdered(true);
            altTextForm.SetArrayAlternate(true);
            altTextForm.SetArrayAltText(true);
            _dcArrayForms.Put("dc:description", altTextForm);
            _dcArrayForms.Put("dc:rights", altTextForm);
            _dcArrayForms.Put("dc:title", altTextForm);
        }
    }
}
